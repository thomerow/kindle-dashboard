// Kindle dashboard: draws the picture for the Kindle with SkiaSharp.
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Dashboard;

/// <summary>Area of the picture that triggers an action when it is tapped on the Kindle.</summary>
/// <param name="Id">"item:12" checks off note 12 (or back), "refresh" only redraws</param>
public sealed record Zone(string Id, int X, int Y, int Width, int Height);

public sealed record NoteLine(long Id, string Text, bool Done);
/// <summary>Sunrise and sunset, local time</summary>
public sealed record SunLine(TimeOnly Rise, TimeOnly Set);
/// <param name="Event">e.g. "Glätte"</param>
/// <param name="When">e.g. "bis 10 Uhr", "ab morgen 6 Uhr"</param>
public sealed record AlertLine(string Event, string When);

/// <summary>Everything the picture shows. Two equal models give the same picture (apart from the time of drawing).</summary>
/// <param name="Hour">Start of the current hour, where the rain chart begins</param>
public sealed record ScreenModel(
   DateOnly Date,
   DateTimeOffset Hour,
   string PlaceName,
   WeatherData? Weather,
   List<NoteLine> Notes,
   SunLine? Sun,
   List<AlertLine> Alerts,
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

/// <summary>
/// Draws a <see cref="ScreenModel"/> for the Kindle Paperwhite 3 in landscape, in 16 shades of grey: the notes in large
/// letters on the left, today's weather in a narrow column on the right.
/// </summary>
public sealed class Renderer {
   /// <summary>Size of the picture in landscape</summary>
   public const int Width = 1448;
   public const int Height = 1072;

   private const float Margin = 40;
   private const float Bottom = Height - Margin;
   private const float SideWidth = 380;
   private const float SideX = Width - Margin - SideWidth;
   private const float Divider = SideX - 28;
   private const float ListRight = Divider - 28;

   /// <summary>Font sizes of the notes, the largest one at which all of them fit is taken.</summary>
   private static readonly float[] s_noteSizes = [104, 92, 80, 68, 60];
   private const int MaxLinesPerNote = 3;
   private const float LineSpacing = 1.2f;
   /// <summary>Rain chart: one bar per hour, starting with the current one</summary>
   private const int ChartHours = 24;

   private static readonly CultureInfo s_german = CultureInfo.GetCultureInfo("de-DE");
   private static readonly SKColor s_black = SKColors.Black;
   private static readonly SKColor s_grey = new(0x77, 0x77, 0x77);
   private static readonly SKColor s_light = new(0xCC, 0xCC, 0xCC);

   private readonly Orientation m_orientation;
   private readonly SKTypeface m_regular;
   private readonly SKTypeface m_semiBold;
   private readonly SKTypeface m_bold;
   private readonly SKTypeface m_emoji;

   public Renderer(Orientation orientation = Orientation.ButtonRight) {
      m_orientation = orientation;
      var fonts = Path.Combine(AppContext.BaseDirectory, "Fonts");
      m_regular = SKTypeface.FromFile(Path.Combine(fonts, "NotoSans-Regular.ttf"));
      m_semiBold = SKTypeface.FromFile(Path.Combine(fonts, "NotoSans-SemiBold.ttf"));
      m_bold = SKTypeface.FromFile(Path.Combine(fonts, "NotoSans-Bold.ttf"));
      m_emoji = SKTypeface.FromFile(Path.Combine(fonts, "NotoEmoji-Regular.ttf"));
   }

   public Rendering Render(ScreenModel model, DateTime localNow, TimeZoneInfo zone) {
      // Skia smooths all edges itself (anti-aliasing), drawing larger and scaling down showed no visible gain
      using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
      var canvas = surface.Canvas;
      canvas.Clear(SKColors.White);
      var zones = new List<Zone>();

      DrawNotes(canvas, model.Notes, zones);
      Line(canvas, Divider, Margin, Divider, Bottom, 2, s_light);
      DrawSide(canvas, model, localNow, zone);
      zones.Add(Zone("refresh", Divider, 0, Width - Divider, Height));

      using var landscape = surface.Snapshot();
      using var turned = Turn(landscape);
      return new Rendering(ToGreyPng(landscape), zones, ToGreyPng(turned), zones.Select(ToDevice).ToList());
   }

   // --- Notes ---

   /// <summary>A note laid out for one font size.</summary>
   /// <param name="Shortened">The text did not fit into MaxLinesPerNote lines and ends with "…"</param>
   private sealed record Block(NoteLine Note, List<string> Lines, float Height, bool Shortened);

   private void DrawNotes(SKCanvas canvas, List<NoteLine> notes, List<Zone> zones) {
      if (notes.Count == 0) {
         Text(canvas, "Nichts zu tun", (Margin + ListRight) / 2, Height / 2 + 24, 72, m_semiBold, s_light, SKTextAlign.Center);
         return;
      }

      // The largest size at which all notes fit without shortening one; failing that the largest at which all fit
      // shortened; failing that the smallest, with as many notes as fit
      const float available = Bottom - Margin;
      var layouts = s_noteSizes.Select(size => (Size: size, Blocks: Layout(notes, size))).ToList();
      var fitting = layouts.Where(layout => layout.Blocks.Sum(block => block.Height) <= available).ToList();
      var (size, blocks) = fitting.FirstOrDefault(layout => !layout.Blocks.Any(block => block.Shortened)) is { Blocks: not null } clean
         ? clean
         : fitting.Count > 0 ? fitting[0] : layouts[^1];
      var more = 0;
      if (blocks.Sum(block => block.Height) > available) {
         const float moreHeight = 56;
         var used = 0f;
         var shown = 0;
         while (shown < blocks.Count && used + blocks[shown].Height <= available - moreHeight) {
            used += blocks[shown].Height;
            shown++;
         }
         more = blocks.Count - shown;
         blocks = blocks.Take(shown).ToList();
      }

      var radius = size * 0.3f;
      var textX = TextX(size);
      var y = Margin;
      foreach (var block in blocks) {
         var baseline = y + size * 0.95f;
         var center = new SKPoint(Margin + radius, baseline - size * 0.36f);
         if (block.Note.Done) {
            using (var fill = Fill(s_grey))
               canvas.DrawCircle(center, radius, fill);
            Check(canvas, center, radius);
         } else {
            using var ring = Stroke(Math.Max(4, size * 0.06f));
            canvas.DrawCircle(center, radius - size * 0.03f, ring);
         }
         foreach (var line in block.Lines) {
            Text(canvas, line, textX, baseline, size, m_semiBold, block.Note.Done ? s_grey : s_black);
            if (block.Note.Done) {
               var strikeY = baseline - size * 0.3f;
               Line(canvas, textX, strikeY, textX + Measure(line, size, m_semiBold), strikeY, Math.Max(3, size * 0.05f), s_grey);
            }
            baseline += size * LineSpacing;
         }
         zones.Add(Zone($"item:{block.Note.Id}", 0, y - size * 0.15f, Divider, block.Height));
         y += block.Height;
      }
      if (more > 0)
         Text(canvas, $"+ {more} weitere", textX, y + 40, 36, m_regular, s_grey);
   }

   private static float TextX(float size) => Margin + size * 1.0f;

   private List<Block> Layout(List<NoteLine> notes, float size) {
      var width = ListRight - TextX(size);
      return notes.Select(note => {
         var lines = Wrap(note.Text, width, size, m_semiBold);
         var shortened = lines.Count > MaxLinesPerNote || lines.Any(line => Measure(line, size, m_semiBold) > width);
         if (lines.Count > MaxLinesPerNote) {
            // The rest goes into the last line, shortened with "…"
            var rest = string.Join(" ", lines.Skip(MaxLinesPerNote - 1));
            lines = [.. lines.Take(MaxLinesPerNote - 1), Fit(rest, width, size, m_semiBold)];
         }
         // Single words wider than the line are shortened as well
         lines = lines.Select(line => Fit(line, width, size, m_semiBold)).ToList();
         return new Block(note, lines, lines.Count * size * LineSpacing + size * 0.3f, shortened);
      }).ToList();
   }

   // --- Day and weather ---

   private void DrawSide(SKCanvas canvas, ScreenModel model, DateTime localNow, TimeZoneInfo zone) {
      const float x = SideX;
      const float right = SideX + SideWidth;
      var date = model.Date.ToDateTime(TimeOnly.MinValue);
      var weekday = date.ToString("dddd", s_german);
      Text(canvas, weekday, x, Margin + 56, FitSize(weekday, SideWidth, 64, m_bold), m_bold, s_black);
      Text(canvas, date.ToString("d. MMMM", s_german), x, Margin + 102, 32, m_regular, s_black);

      var weather = model.Weather;
      if (weather is null) {
         Text(canvas, "Noch keine Wetterdaten", x, Margin + 220, 30, m_regular, s_grey);
      } else {
         const float top = Margin + 136;
         WeatherIcon.Draw(canvas, weather.Now.Icon, x - 6, top, 130);
         Text(canvas, Degrees(weather.Now.Temperature), x + 140, top + 108, 112, m_bold, s_black);

         const float line = top + 180;
         if (weather.Today is { } today) {
            Text(canvas, $"{Degrees(today.Min)} / {Degrees(today.Max)}", x, line, 36, m_semiBold, s_black);
            var rain = today.Precipitation > 0 ? $"{today.Precipitation.ToString("0.#", s_german)} mm" : "trocken";
            Text(canvas, rain, right, line, 30, m_regular, s_black, SKTextAlign.Right);
         }
         DrawRain(canvas, weather.Hours, model.Hour, zone, x, line + 50, right);
      }
      DrawSunAndAlerts(canvas, model, x, right);

      // Foot of the column: battery, time of the picture, source of the weather
      if (model.Battery is { } battery)
         DrawBattery(canvas, x, Bottom - 96, battery, model.Charging);
      Text(canvas, $"Stand {localNow:HH:mm}", right, Bottom - 66, 26, m_regular, s_grey, SKTextAlign.Right);
      Text(canvas, Fit($"Wetter: DWD · {model.PlaceName}", SideWidth, 22, m_regular), x, Bottom - 4, 22, m_regular, s_grey);
   }

   /// <summary>Small bar chart of the probability of rain, one bar per hour for the next 24 hours.</summary>
   private void DrawRain(SKCanvas canvas, List<WeatherHour> hours, DateTimeOffset start, TimeZoneInfo zone, float x, float top, float right) {
      const float chartHeight = 150;
      var coming = hours
         .Select(hour => (Slot: (int)Math.Round((hour.Time - start).TotalHours), hour.PrecipitationProbability))
         .Where(hour => hour.Slot is >= 0 and < ChartHours && hour.PrecipitationProbability is not null)
         .ToList();
      var chartTop = Heading(canvas, "Regen", x, top) + 14;
      if (coming.Count > 0) {
         var highest = coming.Max(hour => hour.PrecipitationProbability!.Value);
         Text(canvas, $"bis {highest} %", right, top + 26, 26, m_semiBold, s_black, SKTextAlign.Right);
      }

      var baseline = chartTop + chartHeight;
      var slot = (right - x) / ChartHours;
      // Grid: 100 % and 50 % light, midnight as a line from top to bottom, the base line dark
      Line(canvas, x, chartTop, right, chartTop, 1, s_light);
      Line(canvas, x, chartTop + chartHeight / 2, right, chartTop + chartHeight / 2, 1, s_light);
      for (var i = 1; i < ChartHours; i++) {
         if (TimeZoneInfo.ConvertTime(start.AddHours(i), zone).Hour == 0)
            Line(canvas, x + i * slot, chartTop, x + i * slot, baseline, 2, s_light);
      }
      using (var bar = Fill(s_black)) {
         foreach (var (index, probability) in coming) {
            var height = Math.Max(3, chartHeight * Math.Clamp(probability!.Value, 0, 100) / 100f);
            canvas.DrawRoundRect(SKRect.Create(x + index * slot + slot * 0.15f, baseline - height, slot * 0.7f, height), 2, 2, bar);
         }
      }
      Line(canvas, x, baseline, right, baseline, 2, s_black);

      // "jetzt" at the start, then the full hours divisible by six, as far as they keep clear of it and of the end
      Text(canvas, "jetzt", x, baseline + 30, 22, m_semiBold, s_grey);
      for (var i = 5; i < ChartHours; i++) {
         var local = TimeZoneInfo.ConvertTime(start.AddHours(i), zone).Hour;
         if (local % 6 == 0)
            Text(canvas, $"{local}", x + i * slot, baseline + 30, 22, m_regular, s_grey, SKTextAlign.Center);
      }
      if (coming.Count == 0)
         Text(canvas, "keine Angabe", (x + right) / 2, chartTop + chartHeight / 2 - 10, 26, m_regular, s_grey, SKTextAlign.Center);
   }

   /// <summary>Below the rain chart: sunrise and sunset, then the warnings of the DWD, inverted.</summary>
   private void DrawSunAndAlerts(SKCanvas canvas, ScreenModel model, float x, float right) {
      const float sunTop = 686;
      if (model.Sun is { } sun) {
         SunIcon(canvas, x + 20, sunTop + 28, true);
         Text(canvas, $"{sun.Rise:H:mm}", x + 52, sunTop + 36, 32, m_semiBold, s_black);
         SunIcon(canvas, x + 20 + SideWidth / 2, sunTop + 28, false);
         Text(canvas, $"{sun.Set:H:mm}", x + 52 + SideWidth / 2, sunTop + 36, 32, m_semiBold, s_black);
      }

      // At most two warnings fit above the battery, more are counted
      const float boxHeight = 60;
      const int maxAlerts = 2;
      var y = sunTop + 70;
      var shown = model.Alerts.Count > maxAlerts ? maxAlerts - 1 : model.Alerts.Count;
      foreach (var alert in model.Alerts.Take(shown)) {
         using (var fill = Fill(s_black))
            canvas.DrawRoundRect(SKRect.Create(x, y, SideWidth, boxHeight), 8, 8, fill);
         var whenWidth = alert.When.Length == 0 ? 0 : Measure(alert.When, 26, m_regular) + 16;
         Text(canvas, Fit(alert.Event, SideWidth - 32 - whenWidth, 30, m_bold), x + 16, y + 41, 30, m_bold, SKColors.White);
         Text(canvas, alert.When, right - 16, y + 40, 26, m_regular, SKColors.White, SKTextAlign.Right);
         y += boxHeight + 10;
      }
      if (shown < model.Alerts.Count)
         Text(canvas, $"+ {model.Alerts.Count - shown} weitere Warnungen", x, y + 30, 26, m_semiBold, s_black);
   }

   /// <summary>Half a sun on the horizon at (x, horizon), with a small arrow below: up for sunrise, down for sunset.</summary>
   private static void SunIcon(SKCanvas canvas, float x, float horizon, bool rising) {
      const float radius = 9;
      using var stroke = Stroke(3);
      canvas.DrawLine(x - 18, horizon, x + 18, horizon, stroke);
      canvas.DrawArc(SKRect.Create(x - radius, horizon - radius, 2 * radius, 2 * radius), 180, 180, false, stroke);
      foreach (var angle in new[] { 200f, 240f, 270f, 300f, 340f }) {
         var radians = angle * MathF.PI / 180;
         var (cos, sin) = (MathF.Cos(radians), MathF.Sin(radians));
         canvas.DrawLine(x + cos * (radius + 4), horizon + sin * (radius + 4), x + cos * (radius + 9), horizon + sin * (radius + 9), stroke);
      }
      var (outer, inner) = rising ? (horizon + 15f, horizon + 8f) : (horizon + 8f, horizon + 15f);
      using var builder = new SKPathBuilder();
      builder.MoveTo(x - 6, outer);
      builder.LineTo(x, inner);
      builder.LineTo(x + 6, outer);
      using var chevron = builder.Detach();
      canvas.DrawPath(chevron, stroke);
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

   /// <summary>White check mark in a filled circle.</summary>
   private static void Check(SKCanvas canvas, SKPoint center, float radius) {
      using var paint = Stroke(Math.Max(3, radius * 0.22f), SKColors.White);
      using var pathBuilder = new SKPathBuilder();
      pathBuilder.MoveTo(center.X - radius * 0.45f, center.Y + radius * 0.02f);
      pathBuilder.LineTo(center.X - radius * 0.12f, center.Y + radius * 0.35f);
      pathBuilder.LineTo(center.X + radius * 0.48f, center.Y - radius * 0.32f);
      using var path = pathBuilder.Detach();
      canvas.DrawPath(path, paint);
   }

   private static string Degrees(double? value) => value is null ? "–" : $"{Math.Round(value.Value):0}°";
   private static string Percent(int? value) => value is null ? "–" : $"{value} %";

   // --- Text with emojis ---
   // Skia draws a text with one typeface and without shaping. Noto Sans has no emojis, so every text is split into
   // runs: letters from the given typeface, emojis from Noto Emoji (black and white, which suits the e-ink display).

   /// <summary>A piece of text that is drawn with one typeface.</summary>
   private readonly record struct Run(string Text, SKTypeface Typeface);

   private List<Run> Runs(string text, SKTypeface typeface) {
      var runs = new List<Run>();
      var current = new StringBuilder();
      var currentTypeface = typeface;
      foreach (var element in TextElements(text)) {
         var (elementTypeface, visible) = Classify(element, typeface);
         if (visible.Length == 0)
            continue;
         if (elementTypeface != currentTypeface && current.Length > 0) {
            runs.Add(new Run(current.ToString(), currentTypeface));
            current.Clear();
         }
         currentTypeface = elementTypeface;
         current.Append(visible);
      }
      if (current.Length > 0)
         runs.Add(new Run(current.ToString(), currentTypeface));
      return runs;
   }

   /// <summary>
   /// Which typeface draws a character as the reader sees it (a grapheme, e.g. "❤️" or "👍🏽"), and what of it is drawn.
   /// An emoji is one that asks for emoji presentation (U+FE0F), lies in the emoji blocks from U+1F000 on, or is
   /// missing from the text font. Selectors, joiners and skin tones are left out: without shaping a sequence does not
   /// combine anyway, and the plain emoji reads better than its parts. Flags are left out entirely, Noto Emoji has none
   /// and their two letters would stand there on their own.
   /// </summary>
   private (SKTypeface Typeface, string Text) Classify(string element, SKTypeface typeface) {
      var kept = new StringBuilder();
      var presentation = false;
      int? first = null;
      foreach (var rune in element.EnumerateRunes()) {
         // A regional flag (black flag and tags) goes as a whole, not only its tags
         if (rune.Value is >= 0xE0020 and <= 0xE007F)
            return (typeface, "");
         switch (rune.Value) {
            case 0xFE0F:
               presentation = true;
               continue;
            // Text presentation, joiner, skin tones, the letters of flags
            case 0xFE0E or 0x200D or >= 0x1F3FB and <= 0x1F3FF or >= 0x1F1E6 and <= 0x1F1FF:
               continue;
         }
         first ??= rune.Value;
         kept.Append(rune.ToString());
      }
      if (first is not { } codepoint)
         return (typeface, "");
      var emoji = presentation || codepoint >= 0x1F000 || !HasGlyph(typeface, codepoint);
      return (emoji && HasGlyph(m_emoji, codepoint) ? m_emoji : typeface, kept.ToString());
   }

   private static bool HasGlyph(SKTypeface typeface, int codepoint) {
      using var font = new SKFont(typeface);
      return font.ContainsGlyph(codepoint);
   }

   private static List<string> TextElements(string text) {
      var elements = new List<string>();
      var enumerator = StringInfo.GetTextElementEnumerator(text);
      while (enumerator.MoveNext())
         elements.Add(enumerator.GetTextElement());
      return elements;
   }

   private float Measure(string text, float size, SKTypeface typeface) =>
      Runs(text, typeface).Sum(run => {
         using var font = Font(run.Typeface, size);
         return font.MeasureText(run.Text);
      });

   private void Text(SKCanvas canvas, string text, float x, float y, float size, SKTypeface typeface, SKColor color, SKTextAlign align = SKTextAlign.Left) {
      var runs = Runs(text, typeface);
      if (align != SKTextAlign.Left) {
         var width = Measure(text, size, typeface);
         x -= align == SKTextAlign.Center ? width / 2 : width;
      }
      using var paint = Fill(color);
      foreach (var run in runs) {
         using var font = Font(run.Typeface, size);
         canvas.DrawText(run.Text, x, y, SKTextAlign.Left, font, paint);
         x += font.MeasureText(run.Text);
      }
   }

   /// <summary>The font size up to maxSize at which a text fits into the width.</summary>
   private float FitSize(string text, float width, float maxSize, SKTypeface typeface) {
      var measured = Measure(text, maxSize, typeface);
      return measured <= width ? maxSize : MathF.Floor(maxSize * width / measured);
   }

   /// <summary>Shortens a text with "…" until it fits into the width, never inside a character.</summary>
   private string Fit(string text, float width, float size, SKTypeface typeface) {
      if (Measure(text, size, typeface) <= width)
         return text;
      var elements = TextElements(text);
      var count = elements.Count;
      while (count > 1 && Measure(string.Concat(elements.Take(count)) + "…", size, typeface) > width)
         count--;
      return string.Concat(elements.Take(count)).TrimEnd() + "…";
   }

   private List<string> Wrap(string text, float width, float size, SKTypeface typeface) {
      var lines = new List<string>();
      foreach (var paragraph in text.Split('\n')) {
         var line = "";
         // Words of which nothing is drawn (a flag on its own) go with their space
         foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => Runs(word, typeface).Count > 0)) {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (Measure(candidate, size, typeface) <= width || line.Length == 0) {
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
