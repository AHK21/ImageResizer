using AppImageResizerDemo.Helpers;

using ImageResizer.AspNetCore.Funcs;

using ImageResizerDemo.Helpers;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;

using SkiaSharp;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Halyco.Middlewares
{
    // You may need to install the Microsoft.AspNetCore.Http.Abstractions package into your project
    public class ResizeImageMiddleware
    {
        private readonly RequestDelegate _next;
        private IMemoryCache _memoryCache;
        private readonly IWebHostEnvironment _env;
        public ResizeImageMiddleware(RequestDelegate next, IMemoryCache memoryCache, IWebHostEnvironment env)
        {
            _next = next;
            _memoryCache = memoryCache;
            _env = env;
        }

        public async Task Invoke(HttpContext context)
        {

            var path = context.Request.Path;

            var rootPath = _env.WebRootPath ?? _env.ContentRootPath; //use web or content root path


            // hand to next middleware if we are not dealing with an image
            if (context.Request.Query.Count == 0 || !ImageUtilities.IsImagePath(path))
            {
                await _next.Invoke(context);
                return;
            }

            // hand to next middleware if we are dealing with an image but it doesn't have any usable resize querystring params
            var resizeParams = ImageUtilities.GetResizeParams(path, context.Request.Query);
            if (!resizeParams.hasParams)
            {
                await _next.Invoke(context);
                return;
            }

            if (resizeParams.format == "svg")
            {
                await _next.Invoke(context);
                return;
            }

            var provider = new PhysicalFileProvider(rootPath);
            var imagePath = provider.GetFileInfo(path.Value).PhysicalPath;

            // check file lastwrite
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(imagePath);
            if (lastWriteTimeUtc.Year == 1601) // file doesn't exist, pass to next middleware
            {
                await _next.Invoke(context);
                return;
            }

            var imageData = GetImageData(imagePath, resizeParams, lastWriteTimeUtc);

            // write to stream
            context.Response.ContentType = ImageUtilities.GetResponseContentType(resizeParams.format);
            context.Response.ContentLength = imageData.Size;
            await context.Response.Body.WriteAsync(imageData.ToArray(), 0, (int)imageData.Size);

            // cleanup
            imageData.Dispose();

        }

        public SKData GetImageData(string imagePath, ResizeParams resizeParams, DateTime lastWriteTimeUtc)
        {
            // check cache and return if cached
            long cacheKey;
            unchecked
            {
                cacheKey = imagePath.GetHashCode() + lastWriteTimeUtc.ToBinary() + resizeParams.ToString().GetHashCode();
            }

            SKData imageData;
            byte[] imageBytes;
            bool isCached = _memoryCache.TryGetValue<byte[]>(cacheKey, out imageBytes);
            if (isCached)
            {
                return SKData.CreateCopy(imageBytes);
            }

            SKEncodedOrigin origin; // this represents the EXIF orientation
            var bitmap = ImageUtilities.LoadBitmap(File.OpenRead(imagePath), out origin); // always load as 32bit (to overcome issues with indexed color)


            if (resizeParams.w == 0)
            {
                resizeParams.w = bitmap.Width;
            }
            if (resizeParams.h == 0)
            {
                resizeParams.h = bitmap.Height;
            }

             //if autorotate = true, and origin isn't correct for the rotation, rotate it
            if (resizeParams.autorotate && origin != SKEncodedOrigin.TopLeft)
                bitmap = RotateAndFlip.RotateAndFlipImage(bitmap, origin);

            // if either w or h is 0, set it based on ratio of original image
            if (resizeParams.h == 0)
                resizeParams.h = (int)Math.Round(bitmap.Height * (float)resizeParams.w / bitmap.Width);
            else if (resizeParams.w == 0)
                resizeParams.w = (int)Math.Round(bitmap.Width * (float)resizeParams.h / bitmap.Height);

            // if we need to crop, crop the original before resizing
            if (resizeParams.mode == "crop")
                bitmap = Crop.CropImage(bitmap, resizeParams);



            // store padded height and width
            var paddedHeight = resizeParams.h;
            var paddedWidth = resizeParams.w;

            // if we need to pad, or max, set the height or width according to ratio
            if (resizeParams.mode == "pad" || resizeParams.mode == "max")
            {
                var bitmapRatio = (float)bitmap.Width / bitmap.Height;
                var resizeRatio = (float)resizeParams.w / resizeParams.h;

                if (bitmapRatio > resizeRatio) // original is more "landscape"
                    resizeParams.h = (int)Math.Round(bitmap.Height * ((float)resizeParams.w / bitmap.Width));
                else
                    resizeParams.w = (int)Math.Round(bitmap.Width * ((float)resizeParams.h / bitmap.Height));
            }

            // resize
            var resizedImageInfo = new SKImageInfo(resizeParams.w, resizeParams.h, SKImageInfo.PlatformColorType, bitmap.AlphaType);
            var resizedBitmap = bitmap.Resize(resizedImageInfo, SKFilterQuality.High);

            //// optionally pad
            if (resizeParams.mode == "pad")
                resizedBitmap = Padding.PaddingImage(resizedBitmap, paddedWidth, paddedHeight, resizeParams.format != "png");


            // encode
            var resizedImage = SKImage.FromBitmap(resizedBitmap);
            var encodeFormat = resizeParams.format == "png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
            imageData = resizedImage.Encode(encodeFormat, resizeParams.quality);

            // cache the result
            _memoryCache.Set<byte[]>(cacheKey, imageData.ToArray());

            // cleanup
            resizedImage.Dispose();
            bitmap.Dispose();
            resizedBitmap.Dispose();

            return imageData;
        }
    }

    // Extension method used to add the middleware to the HTTP request pipeline.
    public static class ResizeImageMiddlewareExtensions
    {
        public static IApplicationBuilder UseResizeImageMiddleware(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<ResizeImageMiddleware>();
        }
    }
}
