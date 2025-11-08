using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CallstackAnnotator
{
    public partial class ResultsView : UserControl
    {
        private string _rawCallstack = string.Empty;
        private readonly List<CallStackFrame> _frames = new();

        public ResultsView()
        {
            InitializeComponent();

            // Mode setup
            ModeCombo.ItemsSource = Enum.GetValues(typeof(PromptBuilder.Mode));
            ModeCombo.SelectedItem = PromptBuilder.Mode.Explain;

            // Settings -> controls
            FramesTextBox.Text = Math.Max(0, AppSettings.FramesToAnnotateFromTop).ToString();
            MaxLinesTextBox.Text = Math.Max(1, AppSettings.MaxSourceLinesPerFunction).ToString();

            // Template editor initial load and initial prompt build
            UpdateTemplateEditorFromMode();
            BuildPrompt();
        }

        // ---------- Top bar actions ----------

        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            string text = "";
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this)!,
                    "Clipboard read failed: " + ex.Message, "Clipboard",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show(Window.GetWindow(this)!,
                    "Clipboard is empty or does not contain text.",
                    "No callstack", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var frames = CallStackParser.Parse(text);
            if (frames.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this)!,
                    "No frames were parsed. Check the format.",
                    "Parse error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _rawCallstack = text;
            CallstackTextBox.Text = _rawCallstack.TrimEnd();

            _frames.Clear();
            _frames.AddRange(frames);

            FramesListBox.ItemsSource = null;
            FramesListBox.ItemsSource = _frames;
            if (_frames.Count > 0) FramesListBox.SelectedIndex = 0;

            BuildPrompt();
        }

        private void CopyPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(PromptTextBox.Text))
                Clipboard.SetText(PromptTextBox.Text);
        }

        // ---------- Mode & template ----------

        private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTemplateEditorFromMode();
            BuildPrompt();
        }

        private void SaveTemplateButton_Click(object sender, RoutedEventArgs e)
        {
            var mode = (PromptBuilder.Mode)ModeCombo.SelectedItem!;
            PromptTemplates.Set(mode, TemplateTextBox.Text);
            BuildPrompt();
        }

        private void TemplateTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Live rebuild as you edit
            BuildPrompt();
        }

        private void UpdateTemplateEditorFromMode()
        {
            var mode = (PromptBuilder.Mode)ModeCombo.SelectedItem!;
            TemplateTextBox.Text = PromptTemplates.Get(mode);
        }

        // ---------- Frames / sources ----------

        private void FramesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowSelectedFrameSource();
        }

        private void ShowSelectedFrameSource()
        {
            if (FramesListBox.SelectedItem is not CallStackFrame frame) return;

            if (SourceExtractor.TryExtractFunctionSource(frame, out string code, out string msg))
            {
                SourceTextBox.Text = code;
                FrameStatusTextBlock.Text = $"{frame}  —  {msg}";
            }
            else
            {
                SourceTextBox.Text = code; // may be nearby/fallback or empty
                FrameStatusTextBlock.Text = $"{frame}  —  {msg}";
            }
        }

        // ---------- Numeric settings ----------

        private void FramesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(FramesTextBox.Text, out var v))
            {
                v = Math.Clamp(v, 0, 10000);
                if (v != AppSettings.FramesToAnnotateFromTop)
                {
                    AppSettings.FramesToAnnotateFromTop = v;
                    BuildPrompt();
                }
            }
        }

        private void MaxLinesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(MaxLinesTextBox.Text, out var v))
            {
                v = Math.Clamp(v, 1, 10000);
                if (v != AppSettings.MaxSourceLinesPerFunction)
                {
                    AppSettings.MaxSourceLinesPerFunction = v;
                    BuildPrompt();
                }
            }
        }

        // Limit input to digits
        private void NumericOnly(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private void NumericPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                var text = (string)e.DataObject.GetData(typeof(string))!;
                if (!text.All(char.IsDigit)) e.CancelCommand();
            }
            else e.CancelCommand();
        }

        // ---------- Prompt building ----------

        private void BuildPrompt()
        {
            // Only annotate TOP N frames to minimize prompt size
            int n = Math.Max(0, AppSettings.FramesToAnnotateFromTop);
            var withSrc = new List<(CallStackFrame, string?)>(Math.Min(n, _frames.Count));
            for (int i = 0; i < _frames.Count && i < n; i++)
            {
                var f = _frames[i];
                if (SourceExtractor.TryExtractFunctionSource(f, out string code, out _))
                    withSrc.Add((f, code));
                else
                    withSrc.Add((f, null));
            }

            var mode = (PromptBuilder.Mode)ModeCombo.SelectedItem!;
            string template = TemplateTextBox.Text;
            PromptTextBox.Text = PromptBuilder.Build(mode, template, _rawCallstack ?? string.Empty, withSrc);
        }
    }
}

