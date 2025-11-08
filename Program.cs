using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CallstackAnnotator
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public sealed class MainForm : Form
    {
        private readonly Panel _host = new() { Dock = DockStyle.Fill };

        public MainForm()
        {
            Text = "Callstack Digest"; // Title bar rename
            MinimumSize = new Size(640, 360);
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;

            Controls.Add(_host);

            // Use the embedded application icon (set via ApplicationIcon in .csproj)
            // This also makes the icon appear in Explorer, Alt+Tab, etc.
            try
            {
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { /* swallow: icon is optional */ }

            // Always launch directly into the "results" screen (formerly shown after Paste)

            var results = new ResultsControl(

                rawCallstack: string.Empty,

                frames: new List<CallstackAnnotator.CallStackFrame>(),

                initialMode: PromptBuilder.Mode.Explain)

            { Dock = DockStyle.Fill };



            _host.Controls.Add(results);



            // Persist window placement

            WinStateStore.Load(this);

            this.FormClosing += (_, __) => WinStateStore.Save(this);

        }


    }



    /// <summary>

    /// Results UI: top bar (Mode + Paste + Copy), inline template editor, then tabs.

    /// </summary>

    public sealed class ResultsControl : UserControl

    {

        private string _rawCallstack;

        private readonly List<CallStackFrame> _frames;



        private readonly ComboBox _cmbMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };

        private readonly Button _btnPaste = new() { Text = "Paste" };

        private readonly Button _btnCopyPrompt = new() { Text = "Copy Prompt" };

        // Global settings (under Mode)
        private readonly NumericUpDown _numFrames = new() { Minimum = 0, Maximum = 10000, Width = 70 };

        private readonly NumericUpDown _numMaxLines = new() { Minimum = 1, Maximum = 10000, Width = 70 };



        private readonly RichTextBox _rtbPrompt = new() { Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9), WordWrap = false };

        private readonly RichTextBox _rtbCallstack = new() { Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9), WordWrap = false, ReadOnly = true };

        private readonly ListBox _lstFrames = new() { Dock = DockStyle.Fill };

        private readonly RichTextBox _rtbSource = new() { Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9), WordWrap = false, ReadOnly = true };

        private readonly Label _lblFrameStatus = new() { AutoSize = true };



        // Inline template editor (previously lived on its own tab)

        private readonly RichTextBox _rtbTemplate = new() { Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9), WordWrap = false, AcceptsTab = true };

        private readonly Button _btnSaveTemplate = new() { Text = "Save Template" };



        public ResultsControl(string rawCallstack, List<CallStackFrame> frames, PromptBuilder.Mode initialMode)

        {

            _rawCallstack = rawCallstack;

            _frames = frames;



            // --- Top bar ---

            var top = new FlowLayoutPanel

            {

                Dock = DockStyle.Top,

                AutoSize = true,

                AutoSizeMode = AutoSizeMode.GrowAndShrink,

                Padding = new Padding(8),

                FlowDirection = FlowDirection.LeftToRight

            };

            _cmbMode.Items.AddRange(new object[] { PromptBuilder.Mode.Empty, PromptBuilder.Mode.Explain, PromptBuilder.Mode.Optimize });

            _cmbMode.SelectedItem = initialMode;



            top.Controls.Add(new Label { Text = "Mode:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 8, 4, 0) });

            top.Controls.Add(_cmbMode);

            top.Controls.Add(_btnPaste);       // NEW

            top.Controls.Add(_btnCopyPrompt);



            // --- Global settings (persisted) ---

            // Frames to Annotate

            top.Controls.Add(new Label { Text = "Frames to Annotate:", AutoSize = true, Padding = new Padding(16, 8, 4, 0) });

            _numFrames.Value = Math.Max(0, AppSettings.FramesToAnnotateFromTop);

            top.Controls.Add(_numFrames);

            // Max Function Lines

            top.Controls.Add(new Label { Text = "Max Function Lines:", AutoSize = true, Padding = new Padding(16, 8, 4, 0) });

            _numMaxLines.Value = Math.Max(1, AppSettings.MaxSourceLinesPerFunction);

            top.Controls.Add(_numMaxLines);



            // --- Inline template editor (below buttons, above tabs) ---

            var templatePanel = new TableLayoutPanel

            {

                Dock = DockStyle.Top,

                ColumnCount = 1,

                RowCount = 2,

                Padding = new Padding(8)

            };

            templatePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            templatePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 420f)); // tripled editor height



            var templateBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0) };

            templateBar.Controls.Add(new Label

            {

                Text = "Prompt template (persists per Mode):",

                AutoSize = true,

                Padding = new Padding(0, 8, 12, 0)

            });

            templateBar.Controls.Add(_btnSaveTemplate);



            _rtbTemplate.Height = 420;

            _rtbTemplate.ScrollBars = RichTextBoxScrollBars.Both;



            templatePanel.Controls.Add(templateBar, 0, 0);

            templatePanel.Controls.Add(_rtbTemplate, 0, 1);



            // --- Tabs ---

            var tabs = new TabControl { Dock = DockStyle.Fill };



            // Prompt

            var tabPrompt = new TabPage("Prompt");

            tabPrompt.Controls.Add(_rtbPrompt);

            tabs.TabPages.Add(tabPrompt);



            // Callstack

            var tabStack = new TabPage("Callstack");

            _rtbCallstack.Text = _rawCallstack.TrimEnd();

            tabStack.Controls.Add(_rtbCallstack);

            tabs.TabPages.Add(tabStack);



            // Sources

            var tabSrc = new TabPage("Sources");

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 380 };

            split.Panel1.Controls.Add(_lstFrames);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };

            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            right.Controls.Add(_lblFrameStatus, 0, 0);

            right.Controls.Add(_rtbSource, 0, 1);

            split.Panel2.Controls.Add(right);

            tabSrc.Controls.Add(split);

            tabs.TabPages.Add(tabSrc);



            // Compose layout

            Controls.Add(tabs);         // Fill

            Controls.Add(templatePanel); // Top (template editor)

            Controls.Add(top);          // Top (buttons)



            // Populate frames list

            if (_frames.Count > 0) _lstFrames.Items.AddRange(_frames.Cast<object>().ToArray());



            // Events

            _lstFrames.SelectedIndexChanged += (_, __) => ShowSelectedFrameSource();



            _btnCopyPrompt.Click += (_, __) =>

            {

                if (!string.IsNullOrEmpty(_rtbPrompt.Text))

                    Clipboard.SetText(_rtbPrompt.Text);

            };



            _cmbMode.SelectedIndexChanged += (_, __) =>

            {

                UpdateTemplateEditorFromMode();

                BuildPrompt();

            };



            _btnSaveTemplate.Click += (_, __) =>

            {

                var mode = (PromptBuilder.Mode)_cmbMode.SelectedItem!;

                PromptTemplates.Set(mode, _rtbTemplate.Text);

                BuildPrompt();

            };



            _btnPaste.Click += (_, __) => PasteFromClipboard();



            _rtbTemplate.TextChanged += (_, __) => BuildPrompt(); // live rebuild as you edit



            // Persisted settings handlers

            _numFrames.ValueChanged += (_, __) =>

            {

                AppSettings.FramesToAnnotateFromTop = (int)_numFrames.Value;

                BuildPrompt();

            };

            _numMaxLines.ValueChanged += (_, __) =>

            {

                AppSettings.MaxSourceLinesPerFunction = (int)_numMaxLines.Value;

                BuildPrompt();

            };



            // Default select the first (top) frame if present

            if (_lstFrames.Items.Count > 0) _lstFrames.SelectedIndex = 0;



            // Initialize template editor and build prompt

            UpdateTemplateEditorFromMode();

            BuildPrompt();

        }



        private void PasteFromClipboard()

        {

            string text = "";

            try

            {

                if (Clipboard.ContainsText()) text = Clipboard.GetText();

            }

            catch (Exception ex)

            {

                MessageBox.Show(this.FindForm(), "Clipboard read failed: " + ex.Message);

                return;

            }



            if (string.IsNullOrWhiteSpace(text))

            {

                MessageBox.Show(this.FindForm(), "Clipboard is empty or does not contain text.", "No callstack",

                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                return;

            }



            var frames = CallStackParser.Parse(text);

            if (frames.Count == 0)

            {

                MessageBox.Show(this.FindForm(), "No frames were parsed. Check the format.", "Parse error",

                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return;

            }



            _rawCallstack = text;

            _rtbCallstack.Text = _rawCallstack.TrimEnd();



            _frames.Clear();

            _frames.AddRange(frames);



            _lstFrames.Items.Clear();

            _lstFrames.Items.AddRange(_frames.Cast<object>().ToArray());

            if (_lstFrames.Items.Count > 0) _lstFrames.SelectedIndex = 0;



            BuildPrompt();

        }



        private void ShowSelectedFrameSource()

        {

            if (_lstFrames.SelectedItem is not CallStackFrame frame) return;



            if (SourceExtractor.TryExtractFunctionSource(frame, out string code, out string msg))

            {

                _rtbSource.Text = code;

                _lblFrameStatus.Text = $"{frame}  —  {msg}";

            }

            else

            {

                _rtbSource.Text = code; // nearby/fallback text or empty

                _lblFrameStatus.Text = $"{frame}  —  {msg}";

            }

        }



        private void BuildPrompt()

        {

            // Annotate only the TOP N frames; emit nothing for the rest (minimize prompt size).

            int n = Math.Max(0, AppSettings.FramesToAnnotateFromTop);

            var src = new List<(CallStackFrame, string?)>(Math.Min(n, _frames.Count));

            for (int i = 0; i < _frames.Count && i < n; i++)

            {

                var f = _frames[i];

                if (SourceExtractor.TryExtractFunctionSource(f, out string code, out _))

                    src.Add((f, code));

                else

                    src.Add((f, null));

            }



            var mode = (PromptBuilder.Mode)_cmbMode.SelectedItem!;

            string template = _rtbTemplate.Text; // Use the inline editor's text directly

            _rtbPrompt.Text = PromptBuilder.Build(mode, template, _rawCallstack ?? string.Empty, src);

        }



        private void UpdateTemplateEditorFromMode()

        {

            var mode = (PromptBuilder.Mode)_cmbMode.SelectedItem!;

            _rtbTemplate.Text = PromptTemplates.Get(mode);

        }

    }



    /// <summary>

    /// Simple registry-backed window placement persistence (HKCU\Software\CallstackAnnotator).

    /// </summary>

    internal static class WinStateStore

    {

        private const string RegPath = @"Software\CallstackAnnotator";



        public static void Load(Form form)

        {

            try

            {

                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath);

                if (key == null) return;

                int x = key.GetValue("X") is int vx ? vx : int.MinValue;

                int y = key.GetValue("Y") is int vy ? vy : int.MinValue;

                int w = key.GetValue("W") is int vw ? vw : int.MinValue;

                int h = key.GetValue("H") is int vh ? vh : int.MinValue;

                var stateStr = key.GetValue("WindowState") as string;



                if (w > 100 && h > 100 && x != int.MinValue && y != int.MinValue)

                {

                    var rect = new Rectangle(x, y, w, h);

                    bool visible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));

                    if (visible)

                    {

                        form.StartPosition = FormStartPosition.Manual;

                        form.SetDesktopBounds(rect.X, rect.Y, rect.Width, rect.Height);

                    }

                }

                if (Enum.TryParse(stateStr, out FormWindowState ws))

                    form.WindowState = ws;

            }

            catch { /* swallow */ }

        }



        public static void Save(Form form)

        {

            try

            {

                var state = form.WindowState;

                // When maximized/minimized, save the restore bounds so we return to user's last normal size/location.

                var rect = state == FormWindowState.Normal ? form.DesktopBounds : form.RestoreBounds;

                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath);

                key?.SetValue("X", rect.X);

                key?.SetValue("Y", rect.Y);

                key?.SetValue("W", rect.Width);

                key?.SetValue("H", rect.Height);

                key?.SetValue("WindowState", state.ToString());

            }

            catch { /* swallow */ }

        }

    }

}
