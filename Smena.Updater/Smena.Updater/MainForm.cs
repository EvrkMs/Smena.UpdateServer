using System.Drawing;
using System.Windows.Forms;

namespace Smena.Updater;

internal sealed class MainForm : Form, IUpdaterInteraction
{
    private readonly UpdaterOptions options;
    private readonly Label statusLabel;
    private readonly Label loadingIconLabel;
    private readonly System.Windows.Forms.Timer loadingIconTimer;

    private bool started;
    private bool isRunning;
    private int loadingIconFrame;

    public int ExitCode { get; private set; } = 1;

    public MainForm(UpdaterOptions options)
    {
        this.options = options;

        Text = options.Mode switch
        {
            UpdaterMode.Reconfigure => "Smena Reconfig",
            UpdaterMode.Uninstall => "Smena Uninstall",
            _ => "Smena Updater"
        };
        ClientSize = new Size(620, 180);
        MinimumSize = new Size(620, 180);
        MaximumSize = new Size(620, 180);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var titleLabel = new Label
        {
            Text = options.Mode switch
            {
                UpdaterMode.Reconfigure => "Настройка клиента",
                UpdaterMode.Uninstall => "Удаление клиента",
                _ => "Обновление клиента"
            },
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(16, 12, 16, 0),
            Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold, GraphicsUnit.Point)
        };

        var statusPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 42
        };

        loadingIconLabel = new Label
        {
            Text = "|",
            AutoSize = false,
            Width = 20,
            Height = 28,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(16, 8),
            Font = new Font("Consolas", 12F, FontStyle.Bold, GraphicsUnit.Point)
        };

        statusLabel = new Label
        {
            Text = "Подготовка...",
            AutoSize = false,
            Width = 560,
            Height = 28,
            Location = new Point(44, 8),
            TextAlign = ContentAlignment.MiddleLeft
        };

        loadingIconTimer = new System.Windows.Forms.Timer
        {
            Interval = 120
        };
        loadingIconTimer.Tick += (_, _) => AdvanceLoadingIcon();

        var spacer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Control
        };

        statusPanel.Controls.Add(loadingIconLabel);
        statusPanel.Controls.Add(statusLabel);

        Controls.Add(spacer);
        Controls.Add(statusPanel);
        Controls.Add(titleLabel);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (started)
        {
            return;
        }

        started = true;
        await RunUpdateAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (isRunning)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    public Task<string?> PromptApiKeyAsync()
    {
        using var dialog = new Form
        {
            Text = "API key",
            Width = 520,
            Height = 210,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var description = new Label
        {
            Text = "Не найден AVA_SMENA_API_KEY. Введите ключ, он сохранится в переменных среды пользователя.",
            AutoSize = false,
            Width = 480,
            Height = 56,
            Location = new Point(12, 10)
        };

        var input = new TextBox
        {
            Width = 480,
            Location = new Point(12, 72),
            UseSystemPasswordChar = true
        };

        var ok = new Button
        {
            Text = "Сохранить",
            DialogResult = DialogResult.OK,
            Width = 120,
            Location = new Point(248, 118),
            Enabled = false
        };

        var cancel = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Width = 120,
            Location = new Point(372, 118)
        };

        input.TextChanged += (_, _) =>
        {
            ok.Enabled = !string.IsNullOrWhiteSpace(input.Text);
        };

        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(description);
        dialog.Controls.Add(input);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);

        var result = dialog.ShowDialog(this);
        return Task.FromResult(result == DialogResult.OK ? input.Text.Trim() : null);
    }

    public Task<string?> PromptGrpcAddressAsync()
    {
        using var dialog = new Form
        {
            Text = "Адрес Smena Server",
            Width = 560,
            Height = 230,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var description = new Label
        {
            Text = "Не найден адрес gRPC сервера. Введите URL (например, https://smena.ava-kk.ru:5001). Адрес сохранится в переменных среды пользователя.",
            AutoSize = false,
            Width = 520,
            Height = 58,
            Location = new Point(12, 10)
        };

        var defaultAddress =
            options.GrpcAddressOverride ??
            Environment.GetEnvironmentVariable("AVA_SMENA_GRPC_ADDRESS", EnvironmentVariableTarget.Process) ??
            Environment.GetEnvironmentVariable("AVA_SMENA_GRPC_ADDRESS", EnvironmentVariableTarget.User) ??
            Environment.GetEnvironmentVariable("AVA_SMENA_GRPC_ADDRESS") ??
            Environment.GetEnvironmentVariable("Grpc__Address") ??
            string.Empty;

        var input = new TextBox
        {
            Width = 520,
            Location = new Point(12, 76),
            Text = defaultAddress
        };

        var validationLabel = new Label
        {
            AutoSize = false,
            Width = 520,
            Height = 20,
            Location = new Point(12, 102),
            ForeColor = Color.DarkRed
        };

        var ok = new Button
        {
            Text = "Сохранить",
            DialogResult = DialogResult.OK,
            Width = 120,
            Location = new Point(280, 132),
            Enabled = UpdaterOptions.TryNormalizeGrpcAddress(input.Text, out _, out _)
        };

        var cancel = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Width = 120,
            Location = new Point(412, 132)
        };

        input.TextChanged += (_, _) =>
        {
            var valid = UpdaterOptions.TryNormalizeGrpcAddress(input.Text, out _, out var error);
            ok.Enabled = valid;
            validationLabel.Text = valid ? string.Empty : error;
        };

        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(description);
        dialog.Controls.Add(input);
        dialog.Controls.Add(validationLabel);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);

        var result = dialog.ShowDialog(this);
        return Task.FromResult(result == DialogResult.OK ? input.Text.Trim() : null);
    }

    public Task<string?> PromptEnvironmentVariableAsync(string name, string prompt, bool secret, string? initialValue)
    {
        using var dialog = new Form
        {
            Text = name,
            Width = 560,
            Height = 230,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var description = new Label
        {
            Text = string.IsNullOrWhiteSpace(prompt)
                ? $"Введите значение переменной {name}."
                : prompt,
            AutoSize = false,
            Width = 520,
            Height = 58,
            Location = new Point(12, 10)
        };

        var input = new TextBox
        {
            Width = 520,
            Location = new Point(12, 76),
            Text = initialValue ?? string.Empty,
            UseSystemPasswordChar = secret
        };

        var ok = new Button
        {
            Text = "Сохранить",
            DialogResult = DialogResult.OK,
            Width = 120,
            Location = new Point(280, 132),
            Enabled = !string.IsNullOrWhiteSpace(input.Text)
        };

        var cancel = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Width = 120,
            Location = new Point(412, 132)
        };

        input.TextChanged += (_, _) => ok.Enabled = !string.IsNullOrWhiteSpace(input.Text);

        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(description);
        dialog.Controls.Add(input);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);

        var result = dialog.ShowDialog(this);
        return Task.FromResult(result == DialogResult.OK ? input.Text.Trim() : null);
    }

    public Task<bool> ConfirmCloseClientAsync(string processName, int processCount)
    {
        var result = MessageBox.Show(
            this,
            $"Найдено запущенных окон клиента: {processCount}.{Environment.NewLine}" +
            "Для обновления клиент нужно закрыть. Закрыть автоматически?",
            "Требуется закрытие клиента",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        return Task.FromResult(result == DialogResult.Yes);
    }

    public Task<bool> ConfirmForceCloseClientAsync(string processName, int processCount)
    {
        var result = MessageBox.Show(
            this,
            $"Клиент все еще запущен ({processCount}).{Environment.NewLine}" +
            "Принудительно завершить процесс?",
            "Принудительное закрытие",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        return Task.FromResult(result == DialogResult.Yes);
    }

    public Task<bool> ConfirmUninstallAsync(string appDirFullPath)
    {
        var result = MessageBox.Show(
            this,
            $"Будет удалена папка клиента:{Environment.NewLine}{appDirFullPath}{Environment.NewLine}{Environment.NewLine}" +
            "Также будут очищены сохраненные AVA_SMENA_API_KEY и AVA_SMENA_GRPC_ADDRESS. Продолжить?",
            "Подтверждение удаления",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        return Task.FromResult(result == DialogResult.Yes);
    }

    private async Task RunUpdateAsync()
    {
        isRunning = true;
        loadingIconTimer.Start();
        statusLabel.Text = "Подготовка...";

        try
        {
            var progress = new Progress<WorkflowProgressUpdate>(ApplyProgress);
            var workflow = new UpdaterWorkflow(options, this, progress);
            var result = await workflow.ExecuteAsync(CancellationToken.None);

            ExitCode = result.ExitCode;
            if (result.ExitCode == 0)
            {
                statusLabel.Text = options.Mode == UpdaterMode.Update ? "Запуск..." : "Готово.";
                await Task.Delay(250);
                isRunning = false;
                loadingIconTimer.Stop();
                Close();
                return;
            }

            loadingIconTimer.Stop();
            statusLabel.Text = "Ошибка выполнения.";
            MessageBox.Show(this, result.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            isRunning = false;
            Close();
            return;
        }
        catch (Exception ex)
        {
            ExitCode = 8;
            loadingIconTimer.Stop();
            statusLabel.Text = "Критическая ошибка.";
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            isRunning = false;
            Close();
            return;
        }
        finally
        {
            isRunning = false;
        }
    }

    private void ApplyProgress(WorkflowProgressUpdate update)
    {
        if (update.Status == WorkflowStageStatus.Failed)
        {
            statusLabel.Text = "Ошибка выполнения.";
            return;
        }

        if (update.Status != WorkflowStageStatus.Running)
        {
            return;
        }

        statusLabel.Text = update.Stage switch
        {
            WorkflowStage.ConnectServer => "Устанавливается соединение с сервером...",
            WorkflowStage.EnsureGrpcAddress => "Проверка адреса сервера...",
            WorkflowStage.EnsureApiKey => "Проверка API ключа...",
            WorkflowStage.FetchManifest => "Проверка версий...",
            WorkflowStage.FetchUpdaterPlan => "Загрузка плана обновления...",
            WorkflowStage.CompareVersions => "Проверка версий...",
            WorkflowStage.EnsureClientStopped => options.Mode == UpdaterMode.Uninstall
                ? "Остановка клиента..."
                : "Обновление клиента...",
            WorkflowStage.DownloadPackage => "Обновление клиента...",
            WorkflowStage.VerifyPackage => "Обновление клиента...",
            WorkflowStage.ExtractPackage => "Обновление клиента...",
            WorkflowStage.ApplyUpdate => "Обновление клиента...",
            WorkflowStage.SaveState => "Обновление клиента...",
            WorkflowStage.LaunchClient => "Запуск...",
            WorkflowStage.ConfirmUninstall => "Подтверждение удаления...",
            WorkflowStage.RemoveClientFiles => "Удаление файлов клиента...",
            WorkflowStage.ClearConfiguration => "Очистка настроек...",
            _ => "Подготовка..."
        };
    }

    private void AdvanceLoadingIcon()
    {
        const string frames = "|/-\\";
        loadingIconFrame = (loadingIconFrame + 1) % frames.Length;
        loadingIconLabel.Text = frames[loadingIconFrame].ToString();
    }
}
