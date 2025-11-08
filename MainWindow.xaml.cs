using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CallstackDigest
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            WinStateStoreWpf.Load(this);

            // Populate mode list
            cboMode.ItemsSource = Enum.GetValues(typeof(PromptBuilder.Mode)).Cast<PromptBuilder.Mode>();
            cboMode.SelectedItem = PromptBuilder.Mode.Explain;

            // Load persisted numbers
            txtFramesTop.Text = AppSettings.FramesToAnnotateFromTop.ToString();
            txtMaxLines.Text  = AppSettings.MaxSourceLinesPerFunction.ToString();

            // Load the current mode's template
            txtTemplate.Text = PromptTemplates.Get(CurrentMode);

            // Optional: paste immediately if clipboard has text
            try
            {
                var clip = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrWhiteSpace(clip))
                {
                    txtCallstack.Text = clip.TrimEnd();
                    _ = RebuildAsync();
                }
            }
            catch { /* clipboard can fail in some contexts */ }
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            WinStateStoreWpf.Save(this);
        }

        private PromptBuilder.Mode CurrentMode => (PromptBuilder.Mode)(cboMode.SelectedItem ?? PromptBuilder.Mode.Explain);

        // ---- Toolbar actions ----

        private void btnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var clip = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrWhiteSpace(clip))
                {
                    txtCallstack.Text = clip.TrimEnd();
                    _ = RebuildAsync();
                    tabs.SelectedIndex = 0; // jump to Prompt tab
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Paste failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtPrompt.Text))
            {
                Clipboard.SetText(txtPrompt.Text, TextDataFormat.UnicodeText);
            }
        }

        private void cboMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            txtTemplate.Text = PromptTemplates.Get(CurrentMode);
            _ = RebuildAsync();
        }

        // ---- Template persistence ----

        private void btnSaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PromptTemplates.Set(CurrentMode, txtTemplate.Text ?? string.Empty);
                lblSaveStatus.Text = "Saved.";
            }
            catch (Exception ex)
            {
                lblSaveStatus.Text = "Save failed.";
                MessageBox.Show(this, ex.Message, "Template Save", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnResetTemplate_Click(object sender, RoutedEventArgs e)
        {
            PromptTemplates.ResetToDefault(CurrentMode);
            txtTemplate.Text = PromptTemplates.Get(CurrentMode);
            lblSaveStatus.Text = "Reset to default.";
            _ = RebuildAsync();
        }

        private void txtTemplate_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Live rebuild as you edit
            _ = RebuildAsync();
        }

        // ---- Numeric inputs (frames/max lines) ----

        private void NumericBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender == txtFramesTop && int.TryParse(txtFramesTop.Text, out int top) && top >= 0)
                AppSettings.FramesToAnnotateFromTop = top;

            if (sender == txtMaxLines && int.TryParse(txtMaxLines.Text, out int max) && max > 0)
                AppSettings.MaxSourceLinesPerFunction = max;

            _ = RebuildAsync();
        }

        private void DigitsOnly(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        // ---- Prompt building ----

        private async Task RebuildAsync()
        {
            string raw = txtCallstack.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                txtPrompt.Clear();
                txtSources.Clear();
                return;
            }

            // Parse callstack
            var frames = await Task.Run(() => CallStackParser.Parse(raw));

            // Choose top N frames to annotate
            int top = Math.Max(0, AppSettings.FramesToAnnotateFromTop);
            var selected = frames.Take(Math.Min(top, frames.Count)).ToList();

            // Extract sources (best effort)
            var framesWithSource = await Task.Run(() =>
            {
                var list = new List<(CallStackFrame Frame, string? Source)>(selected.Count);
                foreach (var f in selected)
                {
                    if (SourceExtractor.TryExtractFunctionSource(f, out var code, out var message))
                        list.Add((f, code));
                    else
                        list.Add((f, code)); // may be empty/nearby – PromptBuilder handles it
                }
                return list;
            });

            // Build prompt with the current template
            string template = txtTemplate.Text ?? string.Empty;
            string prompt = PromptBuilder.Build(CurrentMode, template, raw, framesWithSource);
            txtPrompt.Text = prompt;

            // Fill "Sources" tab (optional, for debugging)
            var sb = new StringBuilder();
            foreach (var (frame, src) in framesWithSource)
            {
                sb.AppendLine($"[Frame {frame.Index}] {frame.Module}!{frame.Symbol}");
                if (!string.IsNullOrEmpty(frame.SourcePath))
                    sb.AppendLine($"File: {frame.SourcePath}:{frame.SourceLine}");
                sb.AppendLine("```cpp");
                sb.AppendLine(string.IsNullOrEmpty(src) ? "// Source not found or could not detect function body." : src);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            txtSources.Text = sb.ToString();
        }
    }
}

