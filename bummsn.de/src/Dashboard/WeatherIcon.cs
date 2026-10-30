// Kindle dashboard: weather icons drawn as vector shapes, so that no icon font or image files are needed.
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using SkiaSharp;

namespace Dashboard;

public static class WeatherIcon {
   private static readonly SKColor s_cloud = new(0xDD, 0xDD, 0xDD);

   /// <summary>Draws the icon of a Bright Sky icon name into the square (x, y, size).</summary>
   public static void Draw(SKCanvas canvas, string? icon, float x, float y, float size) {
      canvas.Save();
      canvas.Translate(x, y);
      canvas.Scale(size / 100f);
      switch (icon) {
         case "clear-day":
            Sun(canvas, 50, 50, 22);
            break;
         case "clear-night":
            Moon(canvas, 50, 50, 28);
            break;
         case "partly-cloudy-day":
            Sun(canvas, 36, 36, 16);
            Cloud(canvas, 14, 40);
            break;
         case "partly-cloudy-night":
            Moon(canvas, 38, 34, 20);
            Cloud(canvas, 14, 40);
            break;
         case "cloudy":
            Cloud(canvas, 4, 22);
            break;
         case "fog":
            Cloud(canvas, 4, 10);
            Lines(canvas, [(16, 74, 84), (10, 86, 70)]);
            break;
         case "wind":
            Wind(canvas);
            break;
         case "rain":
            Cloud(canvas, 4, 10);
            Drops(canvas, false);
            break;
         case "sleet":
            Cloud(canvas, 4, 10);
            Drops(canvas, true);
            break;
         case "snow":
            Cloud(canvas, 4, 10);
            Flakes(canvas);
            break;
         case "hail":
            Cloud(canvas, 4, 10);
            Hail(canvas);
            break;
         case "thunderstorm":
            Cloud(canvas, 4, 10);
            Bolt(canvas);
            break;
         default:
            Unknown(canvas);
            break;
      }
      canvas.Restore();
   }

   private static void Sun(SKCanvas canvas, float cx, float cy, float radius) {
      using var stroke = Renderer.Stroke(5);
      using var fill = Renderer.Fill(SKColors.White);
      canvas.DrawCircle(cx, cy, radius, fill);
      canvas.DrawCircle(cx, cy, radius, stroke);
      for (var i = 0; i < 8; i++) {
         var angle = i * MathF.PI / 4;
         var (sin, cos) = MathF.SinCos(angle);
         canvas.DrawLine(cx + cos * (radius + 8), cy + sin * (radius + 8), cx + cos * (radius + 18), cy + sin * (radius + 18), stroke);
      }
   }

   private static void Moon(SKCanvas canvas, float cx, float cy, float radius) {
      using var outerBuilder = new SKPathBuilder();
      outerBuilder.AddCircle(cx, cy, radius);
      using var innerBuilder = new SKPathBuilder();
      innerBuilder.AddCircle(cx + radius * 0.55f, cy - radius * 0.35f, radius * 0.85f);
      using var outer = outerBuilder.Detach();
      using var inner = innerBuilder.Detach();
      using var crescent = outer.Op(inner, SKPathOp.Difference);
      using var fill = Renderer.Fill(SKColors.White);
      using var stroke = Renderer.Stroke(5);
      canvas.DrawPath(crescent, fill);
      canvas.DrawPath(crescent, stroke);
   }

   /// <summary>A cloud about 90 wide and 50 high, its top left corner at (x, y).</summary>
   private static void Cloud(SKCanvas canvas, float x, float y) {
      using var pathBuilder = new SKPathBuilder();
      pathBuilder.AddCircle(x + 28, y + 34, 18);
      pathBuilder.AddCircle(x + 50, y + 24, 24);
      pathBuilder.AddCircle(x + 70, y + 36, 16);
      // The base starts and ends inside the outer circles, so their round sides form the ends without a kink
      pathBuilder.AddRect(SKRect.Create(x + 28, y + 32, 42, 20));
      using var path = pathBuilder.Detach();
      using var cloud = path.Simplify();
      using var fill = Renderer.Fill(s_cloud);
      using var stroke = Renderer.Stroke(5);
      canvas.DrawPath(cloud, fill);
      canvas.DrawPath(cloud, stroke);
   }

   private static void Drops(SKCanvas canvas, bool withFlakes) {
      using var stroke = Renderer.Stroke(5);
      float[] xs = [30, 50, 70];
      for (var i = 0; i < xs.Length; i++) {
         if (withFlakes && i == 1) {
            Flake(canvas, xs[i] - 3, 82, 8);
            continue;
         }
         canvas.DrawLine(xs[i], 72, xs[i] - 7, 92, stroke);
      }
   }

   private static void Flakes(SKCanvas canvas) {
      Flake(canvas, 30, 78, 8);
      Flake(canvas, 52, 88, 8);
      Flake(canvas, 72, 76, 8);
   }

   private static void Flake(SKCanvas canvas, float cx, float cy, float radius) {
      using var stroke = Renderer.Stroke(4);
      for (var i = 0; i < 3; i++) {
         var (sin, cos) = MathF.SinCos(i * MathF.PI / 3 + MathF.PI / 2);
         canvas.DrawLine(cx - cos * radius, cy - sin * radius, cx + cos * radius, cy + sin * radius, stroke);
      }
   }

   private static void Hail(SKCanvas canvas) {
      using var fill = Renderer.Fill(SKColors.Black);
      canvas.DrawCircle(30, 78, 6, fill);
      canvas.DrawCircle(52, 90, 6, fill);
      canvas.DrawCircle(72, 76, 6, fill);
   }

   private static void Bolt(SKCanvas canvas) {
      using var pathBuilder = new SKPathBuilder();
      pathBuilder.MoveTo(54, 60);
      pathBuilder.LineTo(38, 82);
      pathBuilder.LineTo(50, 82);
      pathBuilder.LineTo(42, 100);
      pathBuilder.LineTo(64, 74);
      pathBuilder.LineTo(52, 74);
      pathBuilder.LineTo(60, 60);
      pathBuilder.Close();
      using var fill = Renderer.Fill(SKColors.Black);
      using var path = pathBuilder.Detach();
      canvas.DrawPath(path, fill);
   }

   private static void Lines(SKCanvas canvas, (float Y, float X0, float X1)[] lines) {
      using var stroke = Renderer.Stroke(5);
      foreach (var (y, x0, x1) in lines)
         canvas.DrawLine(x0, y, x1, y, stroke);
   }

   private static void Wind(SKCanvas canvas) {
      using var stroke = Renderer.Stroke(5);
      using var pathBuilder = new SKPathBuilder();
      pathBuilder.MoveTo(10, 40);
      pathBuilder.LineTo(66, 40);
      pathBuilder.ArcTo(new SKRect(58, 22, 80, 40), 90, -270, false);
      pathBuilder.MoveTo(10, 56);
      pathBuilder.LineTo(80, 56);
      pathBuilder.ArcTo(new SKRect(72, 56, 94, 74), -90, 270, false);
      pathBuilder.MoveTo(20, 72);
      pathBuilder.LineTo(56, 72);
      using var path = pathBuilder.Detach();
      canvas.DrawPath(path, stroke);
   }

   private static void Unknown(SKCanvas canvas) {
      using var stroke = Renderer.Stroke(5);
      canvas.DrawCircle(50, 50, 30, stroke);
      using var font = new SKFont(SKTypeface.Default, 40);
      using var fill = Renderer.Fill(SKColors.Black);
      canvas.DrawText("?", 50, 64, SKTextAlign.Center, font, fill);
   }
}
