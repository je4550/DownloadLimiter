using DownloadLimit.Core;

namespace DownloadLimit.App;

internal sealed class LimitDialog : Form
{
    private readonly NumericUpDown _download, _upload;
    public decimal DownloadMbps => _download.Value;
    public decimal UploadMbps => _upload.Value;

    public LimitDialog(AppSettings settings)
    {
        Text = "Upload / download limits";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(370, 207);
        _download = Input(settings.DownloadMbps, 28);
        _upload = Input(settings.UploadMbps, 70);
        Controls.Add(new Label { Text = "Download (Mbps)", AutoSize = true, Location = new(20, 31) });
        Controls.Add(new Label { Text = "Upload (Mbps)", AutoSize = true, Location = new(20, 73) });
        Controls.Add(_download);
        Controls.Add(_upload);
        Controls.Add(new Label { Text = "Small game/voice packets receive priority.\nDownload shaping acts after packets arrive.",
            AutoSize = true, Location = new(20, 111) });
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new(182, 166), Size = new(78, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new(270, 166), Size = new(78, 28) };
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static NumericUpDown Input(decimal value, int y) => new()
    {
        Minimum = 1, Maximum = 10_000, DecimalPlaces = 3, Increment = 1,
        Value = value, Location = new(182, y), Size = new(165, 27), ThousandsSeparator = true
    };
}
