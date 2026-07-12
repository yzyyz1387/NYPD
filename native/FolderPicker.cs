using System.IO;
using System.Windows.Forms;

static class FolderPicker
{
    public static Task<string?> PickAsync(string initialDirectory)
    {
        var completion = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(initialDirectory) ? initialDirectory : "", UseDescriptionForTitle = true, Description = "选择下载目录" };
                completion.SetResult(dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null);
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
