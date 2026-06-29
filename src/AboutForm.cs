using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LoadView
{
    internal sealed class AboutForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(32, 32, 36);
        private static readonly Color Ink = Color.FromArgb(232, 232, 237);
        private static readonly Color Accent = Color.FromArgb(0x6F, 0xA8, 0xFF);

        public AboutForm()
        {
            Text = "About " + AppInfo.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Bg;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(360, 340);

            Label title = new Label();
            title.Text = AppInfo.Name;
            title.Font = new Font(Font.FontFamily, 18f, FontStyle.Bold);
            title.ForeColor = Ink;
            title.SetBounds(16, 12, 330, 32);
            Controls.Add(title);

            Label ver = new Label();
            ver.Text = "Version " + AppInfo.Version;
            ver.ForeColor = Accent;
            ver.SetBounds(18, 46, 330, 18);
            Controls.Add(ver);

            TextBox log = new TextBox();
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BorderStyle = BorderStyle.FixedSingle;
            log.BackColor = Color.FromArgb(24, 24, 28);
            log.ForeColor = Ink;
            log.Text = string.Join("\r\n", AppInfo.Changelog);
            log.SelectionStart = 0;
            log.SelectionLength = 0;
            log.SetBounds(16, 74, 328, 196);
            Controls.Add(log);

            LinkLabel link = new LinkLabel();
            link.Text = AppInfo.RepoUrl;
            link.LinkColor = Accent;
            link.ActiveLinkColor = Ink;
            link.SetBounds(16, 278, 250, 18);
            link.Click += delegate
            {
                try { Process.Start(AppInfo.RepoUrl); } catch { }
            };
            Controls.Add(link);

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.FlatStyle = FlatStyle.Flat;
            ok.BackColor = Color.FromArgb(56, 56, 64);
            ok.ForeColor = Ink;
            ok.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            ok.SetBounds(ClientSize.Width - 90, 302, 74, 28);
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = ok;
        }
    }
}
