using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PlcsimGateway.Gui
{
    internal static class MarkdownHelpRenderer
    {
        private static readonly Color BackgroundColor = Color.FromArgb(247, 251, 255);
        private static readonly Color TextColor = Color.FromArgb(12, 32, 54);
        private static readonly Color MutedColor = Color.FromArgb(82, 103, 126);
        private static readonly Color AccentColor = Color.FromArgb(0, 96, 176);
        private static readonly Color CodeBackColor = Color.FromArgb(229, 239, 248);
        private static readonly Color TableBackColor = Color.FromArgb(232, 243, 252);
        private static readonly Color QuoteBackColor = Color.FromArgb(226, 241, 251);
        private const int ContentLeftIndent = 12;
        private const int ContentRightIndent = 28;

        public static void Render(RichTextBox textBox, string markdown)
        {
            textBox.Clear();
            textBox.BackColor = BackgroundColor;
            textBox.ForeColor = TextColor;

            string[] lines = NormalizeLines(markdown);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (String.IsNullOrWhiteSpace(trimmed))
                {
                    AppendBlankLine(textBox);
                    continue;
                }

                if (IsTableStart(lines, i))
                {
                    i = RenderTable(textBox, lines, i);
                    continue;
                }

                if (trimmed == "---")
                {
                    RenderSeparator(textBox);
                    continue;
                }

                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    RenderHeading(textBox, trimmed);
                    continue;
                }

                if (trimmed.StartsWith(">", StringComparison.Ordinal))
                {
                    i = RenderQuote(textBox, lines, i);
                    continue;
                }

                if (IsOrderedListItem(trimmed))
                {
                    RenderListItem(textBox, trimmed, false);
                    continue;
                }

                if (IsBulletListItem(trimmed))
                {
                    RenderListItem(textBox, trimmed.Substring(2), true);
                    continue;
                }

                AppendInlineLine(textBox, trimmed, CreateFont(10.5f, FontStyle.Regular), TextColor, 0, 0, BackgroundColor);
            }

            textBox.Select(0, 0);
            textBox.ScrollToCaret();
        }

        private static string[] NormalizeLines(string markdown)
        {
            string safeMarkdown = markdown ?? String.Empty;
            return safeMarkdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static void RenderHeading(RichTextBox textBox, string line)
        {
            int level = 0;
            while (level < line.Length && line[level] == '#')
            {
                level++;
            }

            string text = line.Substring(level).Trim();
            if (textBox.TextLength > 0)
            {
                AppendBlankLine(textBox);
            }

            float size = level <= 1 ? 16.5f : 13.5f;
            AppendStyledText(textBox, text + Environment.NewLine, CreateFont(size, FontStyle.Bold), TextColor, BackgroundColor, 0, 0);
        }

        private static void RenderSeparator(RichTextBox textBox)
        {
            AppendStyledText(textBox, new string('\u2500', 80) + Environment.NewLine, CreateFont(9.0f, FontStyle.Regular), Color.FromArgb(184, 201, 216), BackgroundColor, 0, 0);
            AppendBlankLine(textBox);
        }

        private static bool IsTableStart(string[] lines, int index)
        {
            return index + 1 < lines.Length && IsTableRow(lines[index]) && IsTableSeparator(lines[index + 1]);
        }

        private static bool IsTableRow(string line)
        {
            string trimmed = line.Trim();
            return trimmed.StartsWith("|", StringComparison.Ordinal) && trimmed.EndsWith("|", StringComparison.Ordinal);
        }

        private static bool IsTableSeparator(string line)
        {
            string trimmed = line.Trim();
            if (!IsTableRow(trimmed))
            {
                return false;
            }

            string withoutPipes = trimmed.Trim('|').Trim();
            return Regex.IsMatch(withoutPipes, @"^[:\-\|\s]+$");
        }

        private static int RenderTable(RichTextBox textBox, string[] lines, int startIndex)
        {
            string[] headers = SplitTableCells(lines[startIndex]);
            AppendTableHeader(textBox, headers);

            int index = startIndex + 2;
            while (index < lines.Length && IsTableRow(lines[index]))
            {
                string[] cells = SplitTableCells(lines[index]);
                AppendTableRow(textBox, cells);
                index++;
            }

            AppendBlankLine(textBox);
            return index - 1;
        }

        private static string[] SplitTableCells(string line)
        {
            string[] rawCells = line.Trim().Trim('|').Split('|');
            List<string> cells = new List<string>();
            foreach (string rawCell in rawCells)
            {
                cells.Add(rawCell.Trim());
            }

            return cells.ToArray();
        }

        private static void AppendTableHeader(RichTextBox textBox, string[] headers)
        {
            string headerText = String.Join("    ", headers);
            AppendStyledText(textBox, headerText + Environment.NewLine, CreateFont(10.0f, FontStyle.Bold), TextColor, TableBackColor, 8, 0);
        }

        private static void AppendTableRow(RichTextBox textBox, string[] cells)
        {
            if (cells.Length == 0)
            {
                return;
            }

            AppendInline(textBox, cells[0], CreateFont(10.5f, FontStyle.Bold), AccentColor, 12, 0, BackgroundColor);
            AppendStyledText(textBox, Environment.NewLine, CreateFont(10.5f, FontStyle.Regular), TextColor, BackgroundColor, 12, 0);

            for (int i = 1; i < cells.Length; i++)
            {
                AppendInlineLine(textBox, cells[i], CreateFont(10.2f, FontStyle.Regular), TextColor, 28, 0, BackgroundColor);
            }

            AppendBlankLine(textBox);
        }

        private static int RenderQuote(RichTextBox textBox, string[] lines, int startIndex)
        {
            int index = startIndex;
            while (index < lines.Length && lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                string text = lines[index].TrimStart().Substring(1).Trim();
                if (!String.IsNullOrWhiteSpace(text))
                {
                    AppendInlineLine(textBox, text, CreateFont(10.3f, FontStyle.Italic), TextColor, 16, 0, QuoteBackColor);
                }

                index++;
            }

            AppendBlankLine(textBox);
            return index - 1;
        }

        private static bool IsOrderedListItem(string line)
        {
            return Regex.IsMatch(line, @"^\d+\.\s+");
        }

        private static bool IsBulletListItem(string line)
        {
            return line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
        }

        private static void RenderListItem(RichTextBox textBox, string line, bool bullet)
        {
            string marker = bullet ? "\u2022 " : Regex.Match(line, @"^\d+\.").Value + " ";
            string text = bullet ? line : Regex.Replace(line, @"^\d+\.\s+", String.Empty);
            AppendStyledText(textBox, marker, CreateFont(10.3f, FontStyle.Bold), AccentColor, BackgroundColor, 18, -18);
            AppendInlineLine(textBox, text, CreateFont(10.3f, FontStyle.Regular), TextColor, 18, -18, BackgroundColor);
        }

        private static void AppendInlineLine(RichTextBox textBox, string text, Font baseFont, Color color, int indent, int hangingIndent, Color backColor)
        {
            AppendInline(textBox, text, baseFont, color, indent, hangingIndent, backColor);
            AppendStyledText(textBox, Environment.NewLine, baseFont, color, BackgroundColor, indent, hangingIndent);
        }

        private static void AppendInline(RichTextBox textBox, string text, Font baseFont, Color color, int indent, int hangingIndent, Color backColor)
        {
            int index = 0;
            while (index < text.Length)
            {
                if (TryAppendDelimited(textBox, text, ref index, "**", baseFont, FontStyle.Bold, color, indent, hangingIndent, backColor))
                {
                    continue;
                }

                if (TryAppendDelimited(textBox, text, ref index, "`", CreateFont(9.8f, FontStyle.Regular, "Consolas"), FontStyle.Regular, TextColor, indent, hangingIndent, CodeBackColor))
                {
                    continue;
                }

                if (TryAppendLink(textBox, text, ref index, baseFont, AccentColor, indent, hangingIndent, backColor))
                {
                    continue;
                }

                if (TryAppendDelimited(textBox, text, ref index, "*", baseFont, FontStyle.Italic, color, indent, hangingIndent, backColor))
                {
                    continue;
                }

                int nextMarker = FindNextMarker(text, index);
                string plain = text.Substring(index, nextMarker - index);
                AppendStyledText(textBox, plain, baseFont, color, backColor, indent, hangingIndent);
                index = nextMarker;
            }
        }

        private static bool TryAppendDelimited(
            RichTextBox textBox,
            string text,
            ref int index,
            string delimiter,
            Font baseFont,
            FontStyle style,
            Color color,
            int indent,
            int hangingIndent,
            Color backColor)
        {
            if (!text.Substring(index).StartsWith(delimiter, StringComparison.Ordinal))
            {
                return false;
            }

            int contentStart = index + delimiter.Length;
            int contentEnd = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
            if (contentEnd < 0)
            {
                return false;
            }

            string content = text.Substring(contentStart, contentEnd - contentStart);
            AppendStyledText(textBox, content, CreateFont(baseFont.Size, style, baseFont.FontFamily.Name), color, backColor, indent, hangingIndent);
            index = contentEnd + delimiter.Length;
            return true;
        }

        private static bool TryAppendLink(RichTextBox textBox, string text, ref int index, Font baseFont, Color color, int indent, int hangingIndent, Color backColor)
        {
            if (text[index] != '[')
            {
                return false;
            }

            int textEnd = text.IndexOf("](", index, StringComparison.Ordinal);
            if (textEnd < 0)
            {
                return false;
            }

            int urlEnd = text.IndexOf(')', textEnd + 2);
            if (urlEnd < 0)
            {
                return false;
            }

            string linkText = text.Substring(index + 1, textEnd - index - 1);
            AppendStyledText(textBox, linkText, CreateFont(baseFont.Size, FontStyle.Underline), color, backColor, indent, hangingIndent);
            index = urlEnd + 1;
            return true;
        }

        private static int FindNextMarker(string text, int startIndex)
        {
            int next = text.Length;
            string[] markers = new[] { "**", "`", "[", "*" };
            foreach (string marker in markers)
            {
                int markerIndex = text.IndexOf(marker, startIndex, StringComparison.Ordinal);
                if (markerIndex >= 0 && markerIndex < next)
                {
                    next = markerIndex;
                }
            }

            return next;
        }

        private static void AppendBlankLine(RichTextBox textBox)
        {
            if (textBox.TextLength == 0)
            {
                return;
            }

            AppendStyledText(textBox, Environment.NewLine, CreateFont(7.0f, FontStyle.Regular), TextColor, BackgroundColor, 0, 0);
        }

        private static void AppendStyledText(RichTextBox textBox, string text, Font font, Color color, Color backColor, int indent, int hangingIndent)
        {
            textBox.SelectionIndent = ContentLeftIndent + indent;
            textBox.SelectionHangingIndent = hangingIndent;
            textBox.SelectionRightIndent = ContentRightIndent;
            textBox.SelectionFont = font;
            textBox.SelectionColor = color;
            textBox.SelectionBackColor = backColor;
            textBox.AppendText(text);
            textBox.SelectionBackColor = BackgroundColor;
        }

        private static Font CreateFont(float size, FontStyle style)
        {
            return CreateFont(size, style, "Segoe UI");
        }

        private static Font CreateFont(float size, FontStyle style, string family)
        {
            return new Font(family, size, style);
        }
    }
}
