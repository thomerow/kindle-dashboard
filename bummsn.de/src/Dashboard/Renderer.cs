// Kindle dashboard: draws the picture for the Kindle with SkiaSharp.
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Globalization;
using SkiaSharp;

namespace Dashboard;

/// <summary>Area of the picture that triggers an action when it is tapped on the Kindle.</summary>
/// <param name="Id">"shopping:12" checks off item 12 (or back), "reminder:5" marks reminder 5 as done, "refresh" only redraws</param>
public sealed record Zone(string Id, int X, int Y, int Width, int Height);

public sealed record ReminderLine(long Id, string Text, string? DueLabel, bool Overdue);
public sealed record ShoppingLine(long Id, string Text, bool Done);

/// <summary>Everything the picture shows. Two equal models give the same picture (apart from the time of drawing).</summary>
public sealed record ScreenModel(
   DateOnly Date,
   string PlaceName,
   WeatherData? Weather,
   List<ReminderLine> Reminders,
   List<ShoppingLine> Shopping,
   List<string> Notes,
   int? Battery,
   bool Charging);

/// <summary>
/// The picture twice: in landscape as people see it (web page), and turned into the portrait orientation of the
/// Kindle display. The tap zones likewise, the ones for the Kindle in display (touch) coordinates.
/// </summary>
public sealed record Rendering(byte[] Png, List<Zone> Zones, byte[] DevicePng, List<Zone> DeviceZones);

/// <summary>How the Kindle hangs on the wall. The Paperwhite 3 has its power button on the short edge below the display.</summary>
public enum Orientation {
   /// <summary>Landscape, power button on the right: the Kindle is turned 90° counterclockwise.</summary>
   ButtonRight,
   /// <summary>Landscape, power button on the left: the Kindle is turned 90° clockwise.</summary>
   ButtonLeft
}

/// <summary>Draws a <see cref="ScreenModel"/> for the Kindle Paperwhite 3 in landscape, in 16 shades of grey.</summary>
public sealed class Renderer {
   /// <summary>Size of the picture in landscape</summary>
   public const int Width = 1448;
   public const int Height = 1072;

   private const float Margin = 40;
   private const float Gutter = 48;
   private const float ColumnWidth = (Width - 2 * Margin - 2 * Gutter) / 3;
   private const float Column1 = Margin;
   private const float Column2 = Column1 + ColumnWidth + Gutter;
   private const float Column3 = Column2 + ColumnWidth + Gutter;
   private const float Bottom = Height - Margin;
   private const float ReminderLineHeight = 40;
   private const float ShoppingRow = 58;
   private const float NoteLine = 38;
   private const float NoteGap = 14;

   private static readonly CultureInfo s_german = CultureInfo.GetCultureInfo("de-DE");
   private static readonly SKColor s_black = SKColors.Black;
   private static readonly SKColor s_grey = new(0x77, 0x77, 0x77);
   private static readonly SKColor s_light = new(0xCC, 0xCC, 0xCC);

   private readonly Orientation m_orientation;
   private readonly SKTypeface m_regular;
   private readonly SKTypeface m_semiBold;
   private readonly SKTypeface m_bold;

   public Renderer(Orientation orientation = Orientation.ButtonRight) {
      m_orientation = orientation;
      var fonts = Path.Combine(AppContext.BaseDirectory, "Fonts");
      m_regular = SKTypeface.FromFile(Path.Combine(fonts, "NotoSans-Regular.ttf"));
      m_semiBold = SKTypeface.FromFile(Path.Combine(fonts, "NotoSans-SemiBold.ttf"));
      m_bold = SKTypeface.FromFile(Path.Combine(fonts, "NotoSans-Bold.ttf"));
   }

   public Rendering Render(ScreenModel model, DateTime localNow, TimeZoneInfo zone) {
      // Skia smooths all edges itself (anti-aliasing), drawing larger and scaling down showed no visible gain
      using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
      var canvas = surface.Canvas;
      canvas.Clear(SKColors.White);
      var zones = new List<Zone>();

      DrawDayAndWeather(canvas, model, localNow, zone);
      zones.Add(Zone("refresh", 0, 0, Column2 - Gutter / 2, Height));
      Line(canvas, Column2 - Gutter / 2, Margin, Column2 - Gutter / 2, Bottom, 2, s_light);
      Line(canvas, Column3 - Gutter / 2, Margin, Column3 - Gutter / 2, Bottom, 2, s_light);

      // Notes get the space they need (at most 45 % of the column), the reminders the rest above them
      var notesHeight = Math.Min(MeasureNotes(model.Notes), (Bottom - Margin) * 0.45f);
      var y = DrawReminders(canvas, model.Reminders, Margin, Bottom - (notesHeight > 0 ? notesHeight + 24 : 0), zones);
      if (model.Notes.Count > 0)
         DrawNotes(canvas, model.Notes, model.Reminders.Count > 0 ? y + 24 : Margin, Bottom);
      DrawShopping(canvas, model.Shopping, zones);

      using var landscape = surface.Snapshot();
      using var turned = Turn(landscape);
      return new Rendering(ToGreyPng(landscape), zones, ToGreyPng(turned), zones.Select(ToDevice).ToList());
   }

   // --- Columns ---

   private void DrawDayAndWeather(SKCanvas canvas, ScreenModel model, DateTime localNow, TimeZoneInfo zone) {
      var x = Column1;
      var right = Column1 + ColumnWidth;
      var date = model.Date.ToDateTime(TimeOnly.MinValue);
      Text(canvas, date.ToString("dddd", s_german), x, Margin + 56, 64, m_bold, s_black);
      Text(canvas, date.ToString("d. MMMM yyyy", s_german), x, Margin + 102, 32, m_regular, s_black);

      var weather = model.Weather;
      if (weather is null) {
         Text(canvas, "Noch keine Wetterdaten", x, Margin + 220, 30, m_regular, s_grey);
      } else {
         var top = Margin + 136;
         WeatherIcon.Draw(canvas, weather.Now.Icon, x - 6, top, 150);
         Text(canvas, Degrees(weather.Now.Temperature), x + 156, top + 120, 120, m_bold, s_black);

         var today = weather.Days.FirstOrDefault();
         var line = top + 186;
         if (today is not null) {
            Text(canvas, $"{Degrees(today.Min)} / {Degrees(today.Max)}", x, line, 36, m_semiBold, s_black);
            Text(canvas, $"Regen {Percent(today.PrecipitationProbability)}", right, line, 28, m_regular, s_black, SKTextAlign.Right);
            line += 40;
            var rain = today.Precipitation > 0 ? $"{today.Precipitation.ToString("0.#", s_german)} mm" : "trocken";
            Text(canvas, weather.Now.WindKmh is { } wind ? $"Wind {wind:0} km/h" : "", x, line, 28, m_regular, s_black);
            Text(canvas, rain, right, line, 28, m_regular, s_black, SKTextAlign.Right);
         }

         // Next hours
         var cellsTop = top + 262;
         var hours = weather.Hours.Take(4).ToList();
         var cellWidth = ColumnWidth / 4;
         for (var i = 0; i < hours.Count; i++) {
            var cx = x + i * cellWidth + cellWidth / 2;
            Text(canvas, TimeZoneInfo.ConvertTime(hours[i].Time, zone).ToString("HH:mm"), cx, cellsTop + 26, 26, m_regular, s_black, SKTextAlign.Center);
            WeatherIcon.Draw(canvas, hours[i].Icon, cx - 32, cellsTop + 36, 64);
            Text(canvas, Degrees(hours[i].Temperature), cx, cellsTop + 132, 28, m_semiBold, s_black, SKTextAlign.Center);
         }

         // The next days, as many as fit above the battery
         var dayTop = cellsTop + 160;
         Line(canvas, x, dayTop, right, dayTop, 2, s_light);
         foreach (var day in weather.Days.Skip(1)) {
            if (dayTop + 62 > Bottom - 110)
               break;
            var name = day.Date.ToDateTime(TimeOnly.MinValue).ToString("dddd", s_german);
            Text(canvas, name, x, dayTop + 46, 30, m_semiBold, s_black);
            WeatherIcon.Draw(canvas, day.Icon, x + 190, dayTop + 6, 54);
            Text(canvas, $"{Degrees(day.Max)} / {Degrees(day.Min)}", right, dayTop + 46, 30, m_regular, s_black, SKTextAlign.Right);
            dayTop += 62;
         }
      }

      // Foot of the column: battery, time of the picture, source of the weather
      if (model.Battery is { } battery)
         DrawBattery(canvas, x, Bottom - 96, battery, model.Charging);
      Text(canvas, $"Stand {localNow:HH:mm}", right, Bottom - 66, 26, m_regular, s_grey, SKTextAlign.Right);
      Text(canvas, Fit($"Wetter: DWD · {model.PlaceName}", ColumnWidth, 22, m_regular), x, Bottom - 4, 22, m_regular, s_grey);
   }

   private float DrawReminders(SKCanvas canvas, List<ReminderLine> reminders, float top, float bottom, List<Zone> zones) {
      if (reminders.Count == 0)
         return top;
      var x = Column2;
      var y = Heading(canvas, "Erinnerungen", x, top);
      using var font = Font(m_regular, 32);
      for (var i = 0; i < reminders.Count; i++) {
         var reminder = reminders[i];
         // The whole text, wrapped onto as many lines as it needs
         var lines = Wrap(reminder.Text, ColumnWidth - 48, font);
         var height = lines.Count * ReminderLineHeight + (reminder.DueLabel is null ? 12 : 46);
         var last = i == reminders.Count - 1;
         if (y + height > bottom - (last ? 0 : 48)) {
            Text(canvas, $"+ {reminders.Count - i} weitere", x + 48, y + 32, 26, m_regular, s_grey);
            return y + 48;
         }
         using (var paint = Stroke(3))
            canvas.DrawCircle(x + 16, y + 28, 15, paint);
         var lineY = y;
         foreach (var line in lines) {
            Text(canvas, line, x + 48, lineY + 40, 32, m_regular, s_black);
            lineY += ReminderLineHeight;
         }
         if (reminder.DueLabel is not null) {
            using var badgeFont = Font(m_semiBold, 22);
            var badge = SKRect.Create(x + 48, lineY + 10, badgeFont.MeasureText(reminder.DueLabel) + 20, 30);
            using var paint = reminder.Overdue ? Fill(s_black) : Stroke(2);
            canvas.DrawRoundRect(badge, 15, 15, paint);
            Text(canvas, reminder.DueLabel, badge.MidX, badge.Bottom - 8, 22, m_semiBold, reminder.Overdue ? SKColors.White : s_black, SKTextAlign.Center);
         }
         zones.Add(Zone($"reminder:{reminder.Id}", x - Gutter / 2, y - 4, ColumnWidth + Gutter, height));
         y += height;
      }
      return y;
   }

   private float MeasureNotes(List<string> notes) {
      if (notes.Count == 0)
         return 0;
      using var font = Font(m_regular, 28);
      return 52 + notes.Sum(note => Wrap(note, ColumnWidth - 24, font).Count * NoteLine + NoteGap);
   }

   private void DrawNotes(SKCanvas canvas, List<string> notes, float top, float bottom) {
      var x = Column2;
      var y = Heading(canvas, "Notizen", x, top);
      using var font = Font(m_regular, 28);
      foreach (var note in notes) {
         var lines = Wrap(note, ColumnWidth - 24, font);
         var fitting = Math.Min(lines.Count, (int)((bottom - y) / NoteLine));
         if (fitting <= 0)
            break;
         using (var bar = Fill(s_black))
            canvas.DrawRect(SKRect.Create(x, y + 6, 5, fitting * NoteLine - 6), bar);
         foreach (var line in lines.Take(fitting)) {
            Text(canvas, line, x + 20, y + 30, 28, m_regular, s_black);
            y += NoteLine;
         }
         y += NoteGap;
      }
   }

   private void DrawShopping(SKCanvas canvas, List<ShoppingLine> shopping, List<Zone> zones) {
      var x = Column3;
      var y = Heading(canvas, "Einkaufsliste", x, Margin);
      if (shopping.Count == 0) {
         Text(canvas, "Nichts zu kaufen", x, y + 38, 30, m_regular, s_grey);
         return;
      }
      var fitting = Math.Max(1, (int)((Bottom - y) / ShoppingRow));
      var shown = shopping.Count > fitting ? shopping.Take(fitting - 1).ToList() : shopping;
      foreach (var item in shown) {
         var box = SKRect.Create(x + 2, y + ShoppingRow / 2 - 16, 32, 32);
         using (var paint = Stroke(3))
            canvas.DrawRect(box, paint);
         if (item.Done)
            Check(canvas, box);
         var text = Fit(item.Text, ColumnWidth - 52, 32, m_regular);
         Text(canvas, text, x + 52, y + ShoppingRow / 2 + 11, 32, m_regular, item.Done ? s_grey : s_black);
         if (item.Done) {
            using var font = Font(m_regular, 32);
            Line(canvas, x + 52, y + ShoppingRow / 2, x + 52 + font.MeasureText(text), y + ShoppingRow / 2, 2, s_grey);
         }
         zones.Add(Zone($"shopping:{item.Id}", x - Gutter / 2, y, ColumnWidth + Gutter / 2 + Margin, ShoppingRow));
         y += ShoppingRow;
      }
      if (shown.Count < shopping.Count)
         Text(canvas, $"+ {shopping.Count - shown.Count} weitere", x + 52, y + ShoppingRow / 2 + 10, 28, m_regular, s_grey);
   }

   // --- Small parts ---

   private float Heading(SKCanvas canvas, string text, float x, float top) {
      Text(canvas, text.ToUpper(s_german), x, top + 26, 26, m_bold, s_grey);
      return top + 42;
   }

   private void DrawBattery(SKCanvas canvas, float x, float y, int level, bool charging) {
      var body = SKRect.Create(x, y, 64, 30);
      using (var paint = Stroke(3))
         canvas.DrawRoundRect(body, 4, 4, paint);
      using (var paint = Fill(s_black)) {
         canvas.DrawRect(SKRect.Create(x + 64, y + 9, 6, 12), paint);
         canvas.DrawRect(SKRect.Create(x + 5, y + 5, 54 * Math.Clamp(level, 0, 100) / 100f, 20), paint);
      }
      Text(canvas, charging ? $"{level} % · lädt" : $"{level} %", x + 84, y + 26, 26, m_semiBold, s_black);
   }

   private static void Check(SKCanvas canvas, SKRect box) {
      using var paint = Stroke(5);
      using var pathBuilder = new SKPathBuilder();
      pathBuilder.MoveTo(box.Left + 6, box.MidY);
      pathBuilder.LineTo(box.Left + 13, box.Bottom - 7);
      pathBuilder.LineTo(box.Right - 5, box.Top + 6);
      using var path = pathBuilder.Detach();
      canvas.DrawPath(path, paint);
   }

   private static string Degrees(double? value) => value is null ? "–" : $"{Math.Round(value.Value):0}°";
   private static string Percent(int? value) => value is null ? "–" : $"{value} %";

   private void Text(SKCanvas canvas, string text, float x, float y, float size, SKTypeface typeface, SKColor color, SKTextAlign align = SKTextAlign.Left) {
      using var font = Font(typeface, size);
      using var paint = Fill(color);
      canvas.DrawText(text, x, y, align, font, paint);
   }

   /// <summary>Shortens a text with "…" until it fits into the width.</summary>
   private string Fit(string text, float width, float size, SKTypeface typeface) {
      using var font = Font(typeface, size);
      if (font.MeasureText(text) <= width)
         return text;
      var length = text.Length;
      while (length > 1 && font.MeasureText(text[..length] + "…") > width)
         length--;
      return text[..length].TrimEnd() + "…";
   }

   private static List<string> Wrap(string text, float width, SKFont font) {
      var lines = new List<string>();
      foreach (var paragraph in text.Split('\n')) {
         var line = "";
         foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureText(candidate) <= width || line.Length == 0) {
               line = candidate;
            } else {
               lines.Add(line);
               line = word;
            }
         }
         lines.Add(line);
      }
      return lines;
   }

   // Greyscale anti-aliasing (no subpixel colours, the display has none), positioned in fractions of a pixel
   private static SKFont Font(SKTypeface typeface, float size) =>
      new(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.Slight };

   internal static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

   internal static SKPaint Stroke(float width, SKColor? color = null) => new() {
      Color = color ?? s_black,
      IsAntialias = true,
      Style = SKPaintStyle.Stroke,
      StrokeWidth = width,
      StrokeCap = SKStrokeCap.Round,
      StrokeJoin = SKStrokeJoin.Round
   };

   private static void Line(SKCanvas canvas, float x0, float y0, float x1, float y1, float width, SKColor? color = null) {
      using var paint = Stroke(width, color);
      canvas.DrawLine(x0, y0, x1, y1, paint);
   }

   private static Zone Zone(string id, float x, float y, float width, float height) {
      var left = Math.Max(0, x);
      var top = Math.Max(0, y);
      return new Zone(id, (int)left, (int)top, (int)Math.Min(width, Width - left), (int)Math.Min(height, Height - top));
   }

   // --- Turning into the orientation of the display ---

   /// <summary>The display of the Kindle is 1072 × 1448 (portrait), the picture is drawn in landscape.</summary>
   private SKImage Turn(SKImage landscape) {
      using var surface = SKSurface.Create(new SKImageInfo(Height, Width, SKColorType.Rgba8888, SKAlphaType.Premul));
      var canvas = surface.Canvas;
      if (m_orientation == Orientation.ButtonRight) {
         // Kindle turned counterclockwise: the picture has to be turned clockwise
         canvas.Translate(Height, 0);
         canvas.RotateDegrees(90);
      } else {
         canvas.Translate(0, Width);
         canvas.RotateDegrees(-90);
      }
      // A turn by exactly 90° maps pixels onto pixels, no filtering needed
      canvas.DrawImage(landscape, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
      return surface.Snapshot();
   }

   /// <summary>Converts a zone of the landscape picture into touch coordinates of the display.</summary>
   private Zone ToDevice(Zone zone) => m_orientation == Orientation.ButtonRight
      ? zone with { X = Height - zone.Y - zone.Height, Y = zone.X, Width = zone.Height, Height = zone.Width }
      : zone with { X = zone.Y, Y = Width - zone.X - zone.Width, Width = zone.Height, Height = zone.Width };

   /// <summary>Converts to 8-bit grey and reduces to the 16 levels the e-ink display can show.</summary>
   private static byte[] ToGreyPng(SKImage image) {
      using var grey = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Gray8, SKAlphaType.Opaque));
      image.ReadPixels(grey.Info, grey.GetPixels(), grey.RowBytes, 0, 0);
      var pixels = grey.GetPixelSpan();
      var buffer = new byte[pixels.Length];
      for (var i = 0; i < pixels.Length; i++)
         buffer[i] = (byte)((pixels[i] + 8) / 17 * 17);
      System.Runtime.InteropServices.Marshal.Copy(buffer, 0, grey.GetPixels(), buffer.Length);
      using var result = SKImage.FromBitmap(grey);
      using var data = result.Encode(SKEncodedImageFormat.Png, 100);
      return data.ToArray();
   }
}
