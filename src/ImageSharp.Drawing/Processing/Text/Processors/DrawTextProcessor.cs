﻿// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Primitives;
using SixLabors.ImageSharp.Processing.Drawing.Brushes;
using SixLabors.ImageSharp.Processing.Drawing.Pens;
using SixLabors.ImageSharp.Processing.Drawing.Processors;
using SixLabors.ImageSharp.Processing.Processors;
using SixLabors.Primitives;
using SixLabors.Shapes;

namespace SixLabors.ImageSharp.Processing.Text.Processors
{
    /// <summary>
    /// Using the brush as a source of pixels colors blends the brush color with source.
    /// </summary>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    internal class DrawTextProcessor<TPixel> : ImageProcessor<TPixel>
        where TPixel : struct, IPixel<TPixel>
    {
        private FillRegionProcessor<TPixel> fillRegionProcessor = null;

        private CachingGlyphRenderer textRenderer;

        /// <summary>
        /// Initializes a new instance of the <see cref="DrawTextProcessor{TPixel}"/> class.
        /// </summary>
        /// <param name="options">The options</param>
        /// <param name="text">The text we want to render</param>
        /// <param name="font">The font we want to render with</param>
        /// <param name="brush">The brush to source pixel colors from.</param>
        /// <param name="pen">The pen to outline text with.</param>
        /// <param name="location">The location on the image to start drawign the text from.</param>
        public DrawTextProcessor(TextGraphicsOptions options, string text, Font font, IBrush<TPixel> brush, IPen<TPixel> pen, PointF location)
        {
            this.Brush = brush;
            this.Options = options;
            this.Text = text;
            this.Pen = pen;
            this.Font = font;
            this.Location = location;
        }

        /// <summary>
        /// Gets or sets the brush.
        /// </summary>
        public IBrush<TPixel> Brush { get; set; }

        /// <summary>
        /// Gets or sets the options
        /// </summary>
        public TextGraphicsOptions Options { get; set; }

        /// <summary>
        /// Gets or sets the text
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Gets or sets the pen used for outlining the text, if Null then we will not outline
        /// </summary>
        public IPen<TPixel> Pen { get; set; }

        /// <summary>
        /// Gets or sets the font used to render the text.
        /// </summary>
        public Font Font { get; set; }

        /// <summary>
        /// Gets or sets the location to draw the text at.
        /// </summary>
        public PointF Location { get; set; }

        protected override void BeforeImageApply(Image<TPixel> source, Rectangle sourceRectangle)
        {
            base.BeforeImageApply(source, sourceRectangle);

            // user slow path if pen is set and fast render for brush only rendering

            // do everythign at the image level as we are deligating the processing down to other processors
            var style = new RendererOptions(this.Font, this.Options.DpiX, this.Options.DpiY, this.Location)
            {
                ApplyKerning = this.Options.ApplyKerning,
                TabWidth = this.Options.TabWidth,
                WrappingWidth = this.Options.WrapTextWidth,
                HorizontalAlignment = this.Options.HorizontalAlignment,
                VerticalAlignment = this.Options.VerticalAlignment
            };

            if (this.Pen != null)
            {
                IPathCollection glyphs = TextBuilder.GenerateGlyphs(this.Text, style);

                var pathOptions = (GraphicsOptions)this.Options;
                if (this.Brush != null)
                {
                    // we will reuse the processor for all fill operations to reduce allocations
                    if (this.fillRegionProcessor == null)
                    {
                        this.fillRegionProcessor = new FillRegionProcessor<TPixel>()
                        {
                            Brush = this.Brush,
                            Options = pathOptions
                        };
                    }

                    foreach (IPath p in glyphs)
                    {
                        this.fillRegionProcessor.Region = new ShapeRegion(p);
                        this.fillRegionProcessor.Apply(source, sourceRectangle);
                    }
                }

                // we will reuse the processor for all fill operations to reduce allocations
                if (this.fillRegionProcessor == null)
                {
                    this.fillRegionProcessor = new FillRegionProcessor<TPixel>()
                    {
                        Brush = this.Pen.StrokeFill,
                        Options = pathOptions
                    };
                }

                foreach (IPath p in glyphs)
                {
                    this.fillRegionProcessor.Region = new ShapePath(p, this.Pen);
                    this.fillRegionProcessor.Apply(source, sourceRectangle);
                }
            }
            else
            {
                this.textRenderer = new CachingGlyphRenderer(source.GetMemoryManager());
                this.textRenderer.Options = (GraphicsOptions)this.Options;
                TextRenderer.RenderTextTo(this.textRenderer, this.Text, style);
            }
        }

        protected override void AfterImageApply(Image<TPixel> source, Rectangle sourceRectangle)
        {
            base.AfterImageApply(source, sourceRectangle);
            this.textRenderer?.Dispose();
            this.textRenderer = null;
        }

        /// <inheritdoc/>
        protected override void OnFrameApply(ImageFrame<TPixel> source, Rectangle sourceRectangle, Configuration configuration)
        {
            // this is a no-op as we have processes all as an image, we should be able to pass out of before email apply a skip frames outcome
            if (this.Pen == null && this.Brush != null && this.textRenderer != null && this.textRenderer.Operations.Count > 0)
            {
                // we have rendered at the image level now we can draw
                List<DrawingOperation> operations = this.textRenderer.Operations;

                using (BrushApplicator<TPixel> app = this.Brush.CreateApplicator(source, sourceRectangle, this.textRenderer.Options))
                {
                    foreach (DrawingOperation operation in operations)
                    {
                        IBuffer2D<float> buffer = operation.Map;
                        int startY = operation.Location.Y;
                        int startX = operation.Location.X;
                        int end = operation.Map.Height;
                        for (int row = 0; row < end; row++)
                        {
                            int y = startY + row;
                            app.Apply(buffer.GetRowSpan(row), startX, y);
                        }
                    }
                }
            }
        }

        private struct DrawingOperation
        {
            public IBuffer2D<float> Map { get; set; }

            public Point Location { get; set; }
        }

        private class CachingGlyphRenderer : IGlyphRenderer, IDisposable
        {
            private PathBuilder builder;

            private Point currentRenderPosition = default(Point);
            private int currentRenderingGlyph = 0;

            private PointF currentPoint = default(PointF);
            private Dictionary<int, Buffer2D<float>> glyphMap = new Dictionary<int, Buffer2D<float>>();

            public CachingGlyphRenderer(MemoryManager memoryManager)
            {
                this.MemoryManager = memoryManager;
                this.builder = new PathBuilder();
            }

            public List<DrawingOperation> Operations { get; } = new List<DrawingOperation>();

            public MemoryManager MemoryManager { get; internal set; }

            public GraphicsOptions Options { get; internal set; }

            public void BeginFigure()
            {
                this.builder.StartFigure();
            }

            public bool BeginGlyph(RectangleF bounds, int cacheKey)
            {
                this.currentRenderPosition = Point.Truncate(bounds.Location);
                this.currentRenderingGlyph = cacheKey;

                if (this.glyphMap.ContainsKey(this.currentRenderingGlyph))
                {
                    // we have already drawn the glyph vectors skip trying again
                    return false;
                }

                // we check to see if we have a render cache and if we do then we render else
                this.builder.Clear();

                // ensure all glyphs render around [zero, zero]  so offset negative root positions so when we draw the glyph we can offet it back
                this.builder.SetOrigin(new PointF(-(int)bounds.X, -(int)bounds.Y));

                return true;
            }

            public void BeginText(RectangleF bounds)
            {
                // not concerned about this one
                this.Operations.Clear();
            }

            public void CubicBezierTo(PointF secondControlPoint, PointF thirdControlPoint, PointF point)
            {
                this.builder.AddBezier(this.currentPoint, secondControlPoint, thirdControlPoint, point);
                this.currentPoint = point;
            }

            public void Dispose()
            {
                foreach (KeyValuePair<int, Buffer2D<float>> m in this.glyphMap)
                {
                    m.Value.Dispose();
                }
            }

            public void EndFigure()
            {
                this.builder.CloseFigure();
            }

            public void EndGlyph()
            {
                if (!this.glyphMap.ContainsKey(this.currentRenderingGlyph))
                {
                    this.RenderToCache();
                }

                this.Operations.Add(new DrawingOperation
                {
                    Location = this.currentRenderPosition,
                    Map = this.glyphMap[this.currentRenderingGlyph]
                });
            }

            private void RenderToCache()
            {
                IPath path = this.builder.Build();

                var size = Rectangle.Ceiling(path.Bounds);
                float subpixelCount = 4;
                float offset = 0.5f;
                if (this.Options.Antialias)
                {
                    offset = 0f; // we are antialising skip offsetting as real antalising should take care of offset.
                    subpixelCount = this.Options.AntialiasSubpixelDepth;
                    if (subpixelCount < 4)
                    {
                        subpixelCount = 4;
                    }
                }

                // take the path inside the path builder, scan thing and generate a Buffer2d representing the glyph and cache it.
                Buffer2D<float> fullBuffer = this.MemoryManager.Allocate2D<float>(size.Width + 1, size.Height + 1, true);
                this.glyphMap.Add(this.currentRenderingGlyph, fullBuffer);
                using (IBuffer<float> bufferBacking = this.MemoryManager.Allocate<float>(path.MaxIntersections))
                using (IBuffer<PointF> rowIntersectionBuffer = this.MemoryManager.Allocate<PointF>(size.Width))
                {
                    float subpixelFraction = 1f / subpixelCount;
                    float subpixelFractionPoint = subpixelFraction / subpixelCount;

                    for (int y = 0; y <= size.Height; y++)
                    {
                        Span<float> scanline = fullBuffer.GetRowSpan(y);
                        bool scanlineDirty = false;
                        float yPlusOne = y + 1;

                        for (float subPixel = (float)y; subPixel < yPlusOne; subPixel += subpixelFraction)
                        {
                            var start = new PointF(path.Bounds.Left - 1, subPixel);
                            var end = new PointF(path.Bounds.Right + 1, subPixel);
                            Span<PointF> intersectionSpan = rowIntersectionBuffer.Span;
                            Span<float> buffer = bufferBacking.Span;
                            int pointsFound = path.FindIntersections(start, end, intersectionSpan);

                            if (pointsFound == 0)
                            {
                                // nothing on this line skip
                                continue;
                            }

                            for (int i = 0; i < pointsFound && i < intersectionSpan.Length; i++)
                            {
                                buffer[i] = intersectionSpan[i].X;
                            }

                            QuickSort(buffer.Slice(0, pointsFound));

                            for (int point = 0; point < pointsFound; point += 2)
                            {
                                // points will be paired up
                                float scanStart = buffer[point];
                                float scanEnd = buffer[point + 1];
                                int startX = (int)MathF.Floor(scanStart + offset);
                                int endX = (int)MathF.Floor(scanEnd + offset);

                                if (startX >= 0 && startX < scanline.Length)
                                {
                                    for (float x = scanStart; x < startX + 1; x += subpixelFraction)
                                    {
                                        scanline[startX] += subpixelFractionPoint;
                                        scanlineDirty = true;
                                    }
                                }

                                if (endX >= 0 && endX < scanline.Length)
                                {
                                    for (float x = endX; x < scanEnd; x += subpixelFraction)
                                    {
                                        scanline[endX] += subpixelFractionPoint;
                                        scanlineDirty = true;
                                    }
                                }

                                int nextX = startX + 1;
                                endX = Math.Min(endX, scanline.Length); // reduce to end to the right edge
                                nextX = Math.Max(nextX, 0);
                                for (int x = nextX; x < endX; x++)
                                {
                                    scanline[x] += subpixelFraction;
                                    scanlineDirty = true;
                                }
                            }
                        }

                        if (scanlineDirty)
                        {
                            if (!this.Options.Antialias)
                            {
                                for (int x = 0; x < size.Width; x++)
                                {
                                    if (scanline[x] >= 0.5)
                                    {
                                        scanline[x] = 1;
                                    }
                                    else
                                    {
                                        scanline[x] = 0;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void Swap(Span<float> data, int left, int right)
            {
                float tmp = data[left];
                data[left] = data[right];
                data[right] = tmp;
            }

            private static void QuickSort(Span<float> data)
            {
                QuickSort(data, 0, data.Length - 1);
            }

            private static void QuickSort(Span<float> data, int lo, int hi)
            {
                if (lo < hi)
                {
                    int p = Partition(data, lo, hi);
                    QuickSort(data, lo, p);
                    QuickSort(data, p + 1, hi);
                }
            }

            private static int Partition(Span<float> data, int lo, int hi)
            {
                float pivot = data[lo];
                int i = lo - 1;
                int j = hi + 1;
                while (true)
                {
                    do
                    {
                        i = i + 1;
                    }
                    while (data[i] < pivot && i < hi);

                    do
                    {
                        j = j - 1;
                    }
                    while (data[j] > pivot && j > lo);

                    if (i >= j)
                    {
                        return j;
                    }

                    Swap(data, i, j);
                }
            }

            public void EndText()
            {
            }

            public void LineTo(PointF point)
            {
                this.builder.AddLine(this.currentPoint, point);
                this.currentPoint = point;
            }

            public void MoveTo(PointF point)
            {
                this.builder.StartFigure();
                this.currentPoint = point;
            }

            public void QuadraticBezierTo(PointF secondControlPoint, PointF point)
            {
                this.builder.AddBezier(this.currentPoint, secondControlPoint, point);
                this.currentPoint = point;
            }
        }
    }
}