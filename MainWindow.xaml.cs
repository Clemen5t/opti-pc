using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;

namespace OptiPC;

public partial class MainWindow : Window
{
    private readonly string backupDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OptiPC", "backup");

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(backupDir);
        Loaded += async (_, _) => await AnalyzeAsync();
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        StatusText.Text = "Analyse en cours...";
        try
        {
            var cpu = QueryWmi("Win32_Processor", "Name");
            var gpu = QueryWmi("Win32_VideoController", "Name");
            var os = QueryWmi("Win32_OperatingSystem", "Caption");
            var ram = QueryWmi("Win32_ComputerSystem", "TotalPhysicalMemory", bytesToGb: true);

            HardwareText.Text = $"OS : {os}\nCPU : {cpu}\nGPU : {gpu}\nRAM : {ram}";
            Log("Analyse matérielle terminée.");

            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();

            NetworkText.Text = nic is null
                ? "Aucune interface active détectée."
                : $"{nic.Name}\nType : {nic.NetworkInterfaceType}\nLien : {nic.Speed / 1_000_000.0:0} Mb/s";

            StatusText.Text = "Analyse terminée.";
        }
        catch (Exception ex)
        {
            Log("Erreur analyse : " + ex.Message);
            StatusText.Text = "Erreur d'analyse.";
        }
    }

    private string QueryWmi(string cls, string property, bool bytesToGb = false)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {cls}");
        foreach (ManagementObject obj in searcher.Get())
        {
            var value = obj[property];
            if (value is null) continue;
            if (bytesToGb && ulong.TryParse(value.ToString(), out var bytes))
                return $"{bytes / 1024d / 1024d / 1024d:0.0} Go";
            return value.ToString() ?? "Inconnu";
        }
        return "Inconnu";
    }

    private async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Sauvegarde et optimisation...";
        try
        {
            await BackupAsync();

            // Windows Game Mode
            await RunPowerShellAsync(@"New-Item -Path 'HKCU:\Software\Microsoft\GameBar' -Force | Out-Null; Set-ItemProperty -Path 'HKCU:\Software\Microsoft\GameBar' -Name 'AllowAutoGameMode' -Type DWord -Value 1 -Force; Set-ItemProperty -Path 'HKCU:\Software\Microsoft\GameBar' -Name 'AutoGameModeEnabled' -Type DWord -Value 1 -Force");
            Log("Mode Jeu Windows activé.");

            // HAGS: supported modern AMD GPU. Value 2 = enabled.
            await RunPowerShellAsync(@"New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Force | Out-Null; Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name 'HwSchMode' -Type DWord -Value 2 -Force");
            Log("Planification GPU accélérée matériellement demandée.");

            // Balanced plan is the Windows/AMD-safe baseline for Ryzen X3D.
            await RunProcessAsync("powercfg", "/setactive SCHEME_BALANCED");
            Log("Plan d'alimentation Équilibré activé.");

            // RSS
            await RunPowerShellAsync(@"Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { try { Enable-NetAdapterRss -Name $_.Name -ErrorAction Stop } catch {} }");
            Log("RSS activé sur les interfaces compatibles.");

            // Low-latency RSC profile: disable only on active physical adapters and save rollback.
            await RunPowerShellAsync(@"Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { try { Disable-NetAdapterRsc -Name $_.Name -IPv4 -IPv6 -ErrorAction Stop } catch {} }");
            Log("RSC désactivé pour le profil faible latence sur les cartes compatibles.");

            // Prevent NIC power saving where supported.
            await RunPowerShellAsync(@"Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { try { Set-NetAdapterPowerManagement -Name $_.Name -AllowComputerToTurnOffDevice Disabled -ErrorAction Stop } catch {} }");
            Log("Économie d'énergie agressive de la carte réseau désactivée si prise en charge.");

            // TCP autotuning remains normal: avoids throughput regressions from old gaming tweak guides.
            await RunProcessAsync("netsh", "interface tcp set global autotuninglevel=normal");
            Log("Auto-tuning TCP conservé sur Normal.");

            await RunProcessAsync("ipconfig", "/flushdns");
            Log("Cache DNS vidé.");

            ProfileText.Text = "Gaming + réseau faible latence";
            StatusText.Text = "Optimisation terminée. Redémarrage conseillé.";
        }
        catch (Exception ex)
        {
            Log("Erreur optimisation : " + ex.Message);
            StatusText.Text = "Erreur pendant l'optimisation.";
        }
    }

    private async Task BackupAsync()
    {
        var ps = @"$b=[ordered]@{};
$b.PowerScheme=(powercfg /getactivescheme | Out-String);
$b.HwSchMode=(Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name HwSchMode -ErrorAction SilentlyContinue).HwSchMode;
$b.GameMode=(Get-ItemProperty -Path 'HKCU:\Software\Microsoft\GameBar' -ErrorAction SilentlyContinue | Select-Object AllowAutoGameMode,AutoGameModeEnabled);
$b.Rss=(Get-NetAdapterRss -ErrorAction SilentlyContinue | Select-Object Name,Enabled);
$b.Rsc=(Get-NetAdapterRsc -ErrorAction SilentlyContinue | Select-Object Name,IPv4Enabled,IPv6Enabled);
$b | ConvertTo-Json -Depth 6";
        var json = await RunPowerShellCaptureAsync(ps);
        var path = Path.Combine(backupDir, "settings.json");
        await File.WriteAllTextAsync(path, json);
        Log("Sauvegarde créée : " + path);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(backupDir, "settings.json");
        if (!File.Exists(path))
        {
            Log("Aucune sauvegarde disponible.");
            return;
        }

        StatusText.Text = "Restauration...";
        try
        {
            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("HwSchMode", out var hags) && hags.ValueKind == JsonValueKind.Number)
                await RunPowerShellAsync($"Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers' -Name HwSchMode -Type DWord -Value {hags.GetInt32()} -Force");

            await RunPowerShellAsync(@"Get-NetAdapter -Physical | ForEach-Object { try { Enable-NetAdapterRsc -Name $_.Name -IPv4 -IPv6 -ErrorAction Stop } catch {} }");
            await RunProcessAsync("powercfg", "/setactive SCHEME_BALANCED");

            ProfileText.Text = "Valeurs restaurées";
            Log("Restauration de base terminée.");
            StatusText.Text = "Restauration terminée. Redémarrage conseillé.";
        }
        catch (Exception ex)
        {
            Log("Erreur restauration : " + ex.Message);
            StatusText.Text = "Erreur de restauration.";
        }
    }

    private async void NetworkTest_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Test réseau...";
        try
        {
            var targets = new[] { "1.1.1.1", "8.8.8.8" };
            foreach (var target in targets)
            {
                var values = new List<long>();
                var ping = new Ping();
                for (int i = 0; i < 8; i++)
                {
                    var r = await ping.SendPingAsync(target, 1500);
                    if (r.Status == IPStatus.Success) values.Add(r.RoundtripTime);
                    await Task.Delay(120);
                }

                if (values.Count > 0)
                {
                    var avg = values.Average();
                    var jitter = values.Count > 1
                        ? values.Zip(values.Skip(1), (a,b) => Math.Abs(a-b)).Average()
                        : 0;
                    Log($"{target} : moyenne {avg:0.0} ms | min {values.Min()} | max {values.Max()} | jitter approx. {jitter:0.0} ms");
                }
                else Log($"{target} : aucune réponse.");
            }
            StatusText.Text = "Test réseau terminé.";
        }
        catch (Exception ex)
        {
            Log("Erreur test réseau : " + ex.Message);
            StatusText.Text = "Erreur test réseau.";
        }
    }

    private async Task RunPowerShellAsync(string script)
    {
        var escaped = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        await RunProcessAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {escaped}");
    }

    private async Task<string> RunPowerShellCaptureAsync(string script)
    {
        var escaped = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {escaped}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        var error = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new Exception(error);
        return output;
    }

    private async Task RunProcessAsync(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var error = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            throw new Exception(error);
    }
}
