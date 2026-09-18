using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace OptiPC;

public partial class MainWindow : Window
{
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OptiPC");
    string BackupPath => Path.Combine(dataDir, "backup.json");
    string ReportPath => Path.Combine(dataDir, "last-report.txt");

    record PingResult(double Avg, long Min, long Max, double Jitter, double Loss)
    {
        public double Score => Avg + (Jitter * 1.5) + (Loss * 8.0);
    }

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(dataDir);
        Loaded += async (_, _) =>
        {
            AdminText.Text = IsAdmin() ? "Administrateur ✓" : "Administrateur requis";
            await AnalyzeAsync();
        };
    }

    bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    void Log(string s) { LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n"); LogBox.ScrollToEnd(); }
    void Status(string s, double p) { StatusText.Text = s; Progress.Value = Math.Clamp(p, 0, 100); }
    string SelectedProfile => (ProfileSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto recommandé";

    private async void Analyze_Click(object s, RoutedEventArgs e) => await AnalyzeAsync();
    private async void Benchmark_Click(object s, RoutedEventArgs e) => await BenchmarkAsync("MANUEL", 24);

    async Task AnalyzeAsync()
    {
        Status("Analyse complète...", 10);
        try
        {
            var cpu = Wmi("Win32_Processor", "Name");
            var gpu = Wmi("Win32_VideoController", "Name");
            var board = Wmi("Win32_BaseBoard", "Product");
            var bios = Wmi("Win32_BIOS", "SMBIOSBIOSVersion");
            var os = Wmi("Win32_OperatingSystem", "Caption");
            var build = Wmi("Win32_OperatingSystem", "BuildNumber");
            var ram = Wmi("Win32_ComputerSystem", "TotalPhysicalMemory", true);
            HardwareText.Text = $"CPU : {cpu}\nGPU : {gpu}\nRAM : {ram}\nCarte mère : {board}\nBIOS : {bios}";

            var hags = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", null);
            var game = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AutoGameModeEnabled", null);
            var dvr = Registry.GetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", null);
            WindowsText.Text = $"{os} (build {build})\nGame Mode : {RegState(game, 1)}\nHAGS : {RegState(hags, 2)}\nCapture Game DVR : {RegState(dvr, 1)}\nÉnergie : {Clean(await Capture("powercfg", "/getactivescheme"))}";

            Status("Analyse réseau...", 40);
            var nic = PrimaryNic();
            if (nic != null)
            {
                var ip = nic.GetIPProperties();
                var gw = ip.GatewayAddresses.FirstOrDefault()?.Address?.ToString() ?? "aucune";
                var dns = string.Join(", ", ip.DnsAddresses.Select(x => x.ToString()));
                var q = PQ(nic.Name);
                var d = await PSC($"Get-NetAdapter -Name {q}|Select Name,InterfaceDescription,DriverVersion,DriverDate,LinkSpeed|Format-List|Out-String");
                var rss = await PSC($"Get-NetAdapterRss -Name {q} -ErrorAction SilentlyContinue|Select Enabled,Profile,NumberOfReceiveQueues|Format-List|Out-String");
                var rsc = await PSC($"Get-NetAdapterRsc -Name {q} -ErrorAction SilentlyContinue|Select IPv4Enabled,IPv6Enabled|Format-List|Out-String");
                NetworkText.Text = $"{nic.Name} ({nic.NetworkInterfaceType})\nLien : {nic.Speed / 1_000_000d:0} Mb/s\nPasserelle : {gw}\nDNS : {dns}\n{Clean(d)}\n{Clean(rss)}\n{Clean(rsc)}";
                AdvancedNetworkBox.Text = await PSC($"Get-NetAdapterAdvancedProperty -Name {q} -ErrorAction SilentlyContinue | Select DisplayName,DisplayValue,RegistryKeyword,RegistryValue | Format-Table -Wrap -AutoSize | Out-String");
            }
            else
            {
                NetworkText.Text = "Aucune interface active détectée.";
                AdvancedNetworkBox.Text = "";
            }

            DriversText.Text = await DriverSummaryAsync();
            StartupBox.Text = await PSC(@"Get-CimInstance Win32_StartupCommand | Select Name,Location,Command | Sort Name | Format-Table -Wrap -AutoSize | Out-String");
            Log("Analyse complète terminée.");
            Status("Prêt.", 100);
        }
        catch (Exception ex)
        {
            Log("ERREUR analyse : " + ex.Message);
            Status("Erreur d'analyse.", 0);
        }
    }

    async Task<string> DriverSummaryAsync()
    {
        var gpu = VideoDrivers();
        var chipset = Clean(await PSC(@"Get-CimInstance Win32_PnPSignedDriver | Where-Object {$_.DeviceName -match 'AMD.*(SMBus|GPIO|PSP|PCI)'} | Sort DriverDate -Descending | Select -First 8 DeviceName,DriverVersion,DriverDate | Format-Table -AutoSize | Out-String"));
        var net = Clean(await PSC(@"Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Select Name,InterfaceDescription,DriverVersion,DriverDate | Format-Table -AutoSize | Out-String"));
        return $"GPU :\n{gpu}\n\nChipset AMD :\n{chipset}\n\nRéseau :\n{net}";
    }

    private async void Optimize_Click(object s, RoutedEventArgs e)
    {
        if (!IsAdmin()) { MessageBox.Show("Relance Opti-PC en administrateur."); return; }

        try
        {
            Status("Benchmark AVANT...", 5);
            var before = await BenchmarkAsync("AVANT", 28);
            Status("Sauvegarde...", 15);
            await BackupAsync();
            await CreateRestorePointAsync();
            Status("Windows Gaming...", 28);
            await ApplyWindowsGamingAsync();
            Status("Réseau : base sûre...", 45);
            await ApplyNetworkBaselineAsync();
            Status("Réseau : test RSC A/B...", 58);
            var rscDecision = await OptimizeRscByMeasurementAsync();
            Status("Réseau : propriétés pilote...", 70);
            await ApplyNicVendorSafeTweaksAsync();
            await Run("ipconfig", "/flushdns");
            Log("Cache DNS vidé. Aucun DNS tiers imposé.");
            Status("Benchmark APRÈS...", 80);
            var after = await BenchmarkAsync("APRÈS", 32);

            ProfileText.Text = $"{SelectedProfile}\nGame Mode activé ; plan Équilibré ; RSS activé ; TCP Auto-Tuning Normal.\n{rscDecision}\nDefender, pare-feu, Windows Update, IPv6, PBO/CO et mitigations de sécurité laissés intacts.";
            var report = $"Opti-PC v0.3 — {DateTime.Now:yyyy-MM-dd HH:mm}\nProfil : {SelectedProfile}\n\nAVANT\n{before}\nAPRÈS\n{after}\nDécision RSC : {rscDecision}\n";
            await File.WriteAllTextAsync(ReportPath, report);

            await AnalyzeAsync();
            Status("Terminé — redémarrage conseillé.", 100);
            MessageBox.Show("Optimisation terminée.\n\nLe réglage RSC a été choisi par mesure A/B.\nUn redémarrage est conseillé pour finaliser les changements graphiques.\nLa sauvegarde permet le retour arrière.", "Opti-PC");
        }
        catch (Exception ex)
        {
            Log("ERREUR optimisation : " + ex.Message);
            Status("Optimisation interrompue.", 0);
        }
    }

    async Task ApplyWindowsGamingAsync()
    {
        await PS(@"New-Item 'HKCU:\Software\Microsoft\GameBar' -Force|Out-Null;Set-ItemProperty 'HKCU:\Software\Microsoft\GameBar' AllowAutoGameMode 1 -Type DWord -Force;Set-ItemProperty 'HKCU:\Software\Microsoft\GameBar' AutoGameModeEnabled 1 -Type DWord -Force");
        await PS(@"New-Item 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Force|Out-Null;Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' HwSchMode 2 -Type DWord -Force");
        if (SelectedProfile.Contains("Compétitif") || SelectedProfile.Contains("Auto"))
        {
            await PS(@"New-Item 'HKCU:\System\GameConfigStore' -Force|Out-Null;Set-ItemProperty 'HKCU:\System\GameConfigStore' GameDVR_Enabled 0 -Type DWord -Force;New-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -Force|Out-Null;Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' AppCaptureEnabled 0 -Type DWord -Force");
            Log("Capture Game DVR en arrière-plan désactivée pour le profil gaming.");
        }
        await Run("powercfg", "/setactive SCHEME_BALANCED");
        Log("Mode Jeu + HAGS demandés ; plan Équilibré actif.");
    }

    async Task ApplyNetworkBaselineAsync()
    {
        await Run("netsh", "int tcp set global rss=enabled");
        await Run("netsh", "int tcp set global autotuninglevel=normal");
        var nic = PrimaryNic();
        if (nic == null) return;
        var q = PQ(nic.Name);
        await PS($"try{{Enable-NetAdapterRss -Name {q} -ErrorAction Stop}}catch{{}}");
        await PS($"try{{Set-NetAdapterPowerManagement -Name {q} -AllowComputerToTurnOffDevice Disabled -ErrorAction Stop}}catch{{}}");
        Log($"RSS activé et économie d'énergie de l'interface réduite sur {nic.Name} si supporté.");
    }

    async Task<string> OptimizeRscByMeasurementAsync()
    {
        var nic = PrimaryNic();
        if (nic == null) return "RSC non testé : aucune interface active.";
        var q = PQ(nic.Name);
        var current = Clean(await PSC($"$r=Get-NetAdapterRsc -Name {q} -ErrorAction SilentlyContinue;if($null -eq $r){{'UNSUPPORTED'}}else{{[string]$r.IPv4Enabled+'|'+[string]$r.IPv6Enabled}}"));
        if (current.Contains("UNSUPPORTED")) return "RSC non supporté par cette interface.";

        var target = nic.GetIPProperties().GatewayAddresses.FirstOrDefault()?.Address?.ToString() ?? "1.1.1.1";
        var initial = await PingStats(target, 34);
        await PS($"try{{Disable-NetAdapterRsc -Name {q} -IPv4 -IPv6 -Confirm:$false -ErrorAction Stop}}catch{{}}");
        await Task.Delay(1200);
        var off = await PingStats(target, 34);

        bool wasV4 = current.StartsWith("True", StringComparison.OrdinalIgnoreCase);
        bool wasV6 = current.EndsWith("True", StringComparison.OrdinalIgnoreCase);
        var improvement = initial.Score <= 0 ? 0 : (initial.Score - off.Score) / initial.Score;
        var keepOff = SelectedProfile.Contains("Compétitif")
            ? off.Loss <= initial.Loss && off.Score <= initial.Score * 1.02
            : off.Loss <= initial.Loss && improvement >= 0.04;

        if (!keepOff)
        {
            var v4 = wasV4 ? "$true" : "$false";
            var v6 = wasV6 ? "$true" : "$false";
            await PS($"try{{Set-NetAdapterRsc -Name {q} -IPv4Enabled {v4} -IPv6Enabled {v6} -Confirm:$false -ErrorAction Stop}}catch{{}}");
            Log($"RSC restauré : test OFF non concluant. Score initial {initial.Score:0.0}, OFF {off.Score:0.0}.");
            return $"RSC conservé/restauré selon l'état initial (score {initial.Score:0.0} vs OFF {off.Score:0.0}).";
        }

        Log($"RSC OFF conservé : score {initial.Score:0.0} → {off.Score:0.0}.");
        return $"RSC désactivé après test A/B (score {initial.Score:0.0} → {off.Score:0.0}).";
    }

    async Task ApplyNicVendorSafeTweaksAsync()
    {
        var nic = PrimaryNic();
        if (nic == null) return;
        var q = PQ(nic.Name);
        var script =
            $"$p=Get-NetAdapterAdvancedProperty -Name {q} -ErrorAction SilentlyContinue;" +
            "$targets=$p|Where-Object {$_.DisplayName -match 'Energy.Efficient|Green Ethernet|Gigabit Lite|Power Saving|Économie.*énergie'};" +
            "foreach($x in $targets){try{" +
            $"if($x.ValidDisplayValues -contains 'Disabled'){{Set-NetAdapterAdvancedProperty -Name {q} -RegistryKeyword $x.RegistryKeyword -DisplayValue 'Disabled' -NoRestart -ErrorAction Stop}}" +
            $"elseif($x.ValidDisplayValues -contains 'Désactivé'){{Set-NetAdapterAdvancedProperty -Name {q} -RegistryKeyword $x.RegistryKeyword -DisplayValue 'Désactivé' -NoRestart -ErrorAction Stop}}" +
            "}catch{}}";
        await PS(script);

        if (nic.Description.Contains("Realtek", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("8125", StringComparison.OrdinalIgnoreCase))
            Log("Realtek détecté : EEE/Green Ethernet/Gigabit Lite désactivés uniquement si le pilote expose une valeur Disabled.");
        else
            Log("Optimisations d'économie d'énergie appliquées uniquement aux propriétés explicitement supportées par le pilote.");
    }

    async Task CreateRestorePointAsync()
    {
        try
        {
            await PS(@"try{$d=$env:SystemDrive+'\';Enable-ComputerRestore -Drive $d -ErrorAction SilentlyContinue;Checkpoint-Computer -Description 'Opti-PC avant optimisation' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop}catch{Write-Output $_.Exception.Message}");
            Log("Point de restauration Windows demandé.");
        }
        catch { Log("Point de restauration indisponible ; la sauvegarde Opti-PC reste active."); }
    }

    async Task BackupAsync()
    {
        var ps = @"
$b=[ordered]@{}
$b.Timestamp=(Get-Date).ToString('o')
$b.Power=(powercfg /getactivescheme|Out-String)
$b.HwSchMode=(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name HwSchMode -ErrorAction SilentlyContinue).HwSchMode
$b.Game=(Get-ItemProperty 'HKCU:\Software\Microsoft\GameBar' -ErrorAction SilentlyContinue|Select AllowAutoGameMode,AutoGameModeEnabled)
$b.GameDVR=(Get-ItemProperty 'HKCU:\System\GameConfigStore' -ErrorAction SilentlyContinue|Select GameDVR_Enabled)
$b.AppCapture=(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -ErrorAction SilentlyContinue|Select AppCaptureEnabled)
$b.Adapters=@()
Get-NetAdapter -Physical -ErrorAction SilentlyContinue|ForEach-Object{
  $n=$_.Name
  $b.Adapters += [ordered]@{
    Name=$n
    Rss=(Get-NetAdapterRss -Name $n -ErrorAction SilentlyContinue|Select Enabled)
    Rsc=(Get-NetAdapterRsc -Name $n -ErrorAction SilentlyContinue|Select IPv4Enabled,IPv6Enabled)
    Power=(Get-NetAdapterPowerManagement -Name $n -ErrorAction SilentlyContinue|Select AllowComputerToTurnOffDevice,WakeOnMagicPacket,WakeOnPattern)
    Advanced=(Get-NetAdapterAdvancedProperty -Name $n -ErrorAction SilentlyContinue|Select RegistryKeyword,RegistryValue)
  }
}
$b|ConvertTo-Json -Depth 12";
        await File.WriteAllTextAsync(BackupPath, await PSC(ps));
        Log("Sauvegarde complète créée : " + BackupPath);
    }

    private async void Restore_Click(object s, RoutedEventArgs e)
    {
        if (!File.Exists(BackupPath)) { MessageBox.Show("Aucune sauvegarde Opti-PC trouvée."); return; }
        try
        {
            Status("Restauration...", 20);
            var path = PSQ(BackupPath);
            var script = $@"
$b=Get-Content -Raw {path}|ConvertFrom-Json
if($b.Power -match '[0-9a-fA-F-]{{36}}'){{powercfg /setactive $Matches[0]|Out-Null}}
if($null -eq $b.HwSchMode){{Remove-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' HwSchMode -ErrorAction SilentlyContinue}}
else{{Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' HwSchMode ([int]$b.HwSchMode) -Type DWord -Force}}
foreach($k in 'AllowAutoGameMode','AutoGameModeEnabled'){{
  if($null -ne $b.Game.$k){{New-Item 'HKCU:\Software\Microsoft\GameBar' -Force|Out-Null;Set-ItemProperty 'HKCU:\Software\Microsoft\GameBar' $k ([int]$b.Game.$k) -Type DWord -Force}}
}}
if($null -ne $b.GameDVR.GameDVR_Enabled){{New-Item 'HKCU:\System\GameConfigStore' -Force|Out-Null;Set-ItemProperty 'HKCU:\System\GameConfigStore' GameDVR_Enabled ([int]$b.GameDVR.GameDVR_Enabled) -Type DWord -Force}}
if($null -ne $b.AppCapture.AppCaptureEnabled){{New-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -Force|Out-Null;Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' AppCaptureEnabled ([int]$b.AppCapture.AppCaptureEnabled) -Type DWord -Force}}
foreach($a in $b.Adapters){{
  $n=[string]$a.Name
  if($null -ne $a.Rss.Enabled){{if([bool]$a.Rss.Enabled){{Enable-NetAdapterRss -Name $n -ErrorAction SilentlyContinue}}else{{Disable-NetAdapterRss -Name $n -ErrorAction SilentlyContinue}}}}
  if($null -ne $a.Rsc.IPv4Enabled){{Set-NetAdapterRsc -Name $n -IPv4Enabled ([bool]$a.Rsc.IPv4Enabled) -IPv6Enabled ([bool]$a.Rsc.IPv6Enabled) -Confirm:$false -ErrorAction SilentlyContinue}}
  if($null -ne $a.Power.AllowComputerToTurnOffDevice){{Set-NetAdapterPowerManagement -Name $n -AllowComputerToTurnOffDevice $a.Power.AllowComputerToTurnOffDevice -ErrorAction SilentlyContinue}}
  foreach($x in $a.Advanced){{try{{$rv=@($x.RegistryValue);Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword ([string]$x.RegistryKeyword) -RegistryValue $rv -NoRestart -ErrorAction Stop}}catch{{}}}}
}}";
            await PS(script);
            Log("Paramètres sauvegardés restaurés.");
            ProfileText.Text = "Restauré depuis la sauvegarde";
            await AnalyzeAsync();
            Status("Restauration terminée.", 100);
            MessageBox.Show("Restauration terminée. Redémarre Windows pour finaliser.", "Opti-PC");
        }
        catch (Exception ex)
        {
            Log("ERREUR restauration : " + ex.Message);
            Status("Erreur restauration.", 0);
        }
    }

    async Task<string> BenchmarkAsync(string label, int count)
    {
        BenchmarkBox.AppendText($"===== {label} — {DateTime.Now:HH:mm:ss} =====\n");
        var targets = new List<string>();
        var nic = PrimaryNic();
        var gw = nic?.GetIPProperties().GatewayAddresses.FirstOrDefault()?.Address?.ToString();
        if (!string.IsNullOrWhiteSpace(gw)) targets.Add(gw);
        targets.AddRange(new[] { "1.1.1.1", "8.8.8.8" });

        var sb = new StringBuilder();
        foreach (var target in targets.Distinct())
        {
            var r = await PingStats(target, count);
            var line = $"{target,-16} moyenne {r.Avg,6:0.0} ms | min {r.Min,4} | max {r.Max,4} | jitter {r.Jitter,5:0.0} ms | pertes {r.Loss,5:0.0}% | score {r.Score,6:0.0}";
            sb.AppendLine(line);
            BenchmarkBox.AppendText(line + "\n");
        }

        var sw = Stopwatch.StartNew();
        try { await Dns.GetHostAddressesAsync("www.microsoft.com"); } catch { }
        sw.Stop();
        var dns = $"Résolution DNS indicative : {sw.Elapsed.TotalMilliseconds:0.0} ms (pas le ping du jeu).";
        sb.AppendLine(dns);
        BenchmarkBox.AppendText(dns + "\n\n");
        BenchmarkBox.ScrollToEnd();
        return sb.ToString();
    }

    static async Task<PingResult> PingStats(string target, int count)
    {
        var values = new List<long>();
        using var p = new Ping();
        for (int i = 0; i < count; i++)
        {
            try { var r = await p.SendPingAsync(target, 1500); if (r.Status == IPStatus.Success) values.Add(r.RoundtripTime); } catch { }
            await Task.Delay(70);
        }
        if (values.Count == 0) return new PingResult(0, 0, 0, 0, 100);
        var jitter = values.Count < 2 ? 0 : values.Zip(values.Skip(1), (a, b) => Math.Abs(a - b)).Average();
        return new PingResult(values.Average(), values.Min(), values.Max(), jitter, 100d * (count - values.Count) / count);
    }

    private async void Drivers_Click(object s, RoutedEventArgs e)
    {
        await AnalyzeAsync();
        var old = await PSC(@"$limit=(Get-Date).AddDays(-120);Get-CimInstance Win32_PnPSignedDriver|Where-Object {($_.DeviceName -match 'AMD|Radeon|Realtek|MediaTek|Wi-Fi|Wireless') -and $_.DriverDate -lt $limit}|Select DeviceName,DriverVersion,DriverDate|Format-Table -AutoSize|Out-String");
        MessageBox.Show("Versions installées actualisées.\n\n" + (string.IsNullOrWhiteSpace(old) ? "Aucun pilote ciblé clairement ancien (>120 jours) détecté.\n\n" : "Des pilotes ciblés ont plus de 120 jours : consulte l'onglet Pilotes.\n\n") + "Opti-PC ne flashe jamais le BIOS et n'installe pas automatiquement un pilote optionnel. Utilise les packages officiels AMD/MSI.", "Pilotes / BIOS");
    }

    NetworkInterface? PrimaryNic() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.GetIPProperties().GatewayAddresses.Any()).OrderByDescending(n => n.Speed).FirstOrDefault();

    string Wmi(string c, string p, bool gb = false)
    {
        using var s = new ManagementObjectSearcher($"SELECT {p} FROM {c}");
        foreach (ManagementObject o in s.Get())
        {
            var v = o[p];
            if (v == null) continue;
            if (gb && ulong.TryParse(v.ToString(), out var b)) return $"{b / 1073741824d:0.0} Go";
            return v.ToString() ?? "Inconnu";
        }
        return "Inconnu";
    }

    string VideoDrivers()
    {
        using var s = new ManagementObjectSearcher("SELECT Name,DriverVersion,DriverDate FROM Win32_VideoController");
        return string.Join("\n", s.Get().Cast<ManagementObject>().Select(o => $"{o["Name"]} — {o["DriverVersion"]} — {o["DriverDate"]}"));
    }

    static string PQ(string s) => "'" + s.Replace("'", "''") + "'";
    static string PSQ(string s) => "'" + s.Replace("'", "''") + "'";
    static string Clean(string s) => Regex.Replace(s ?? "", @"\r?\n\s*\r?\n", "\n").Trim();
    static string RegState(object? v, int on) => v is int i ? (i == on ? "Activé" : i == 0 ? "Désactivé" : $"Valeur {i}") : "Non défini";

    async Task PS(string script) { var e = Convert.ToBase64String(Encoding.Unicode.GetBytes(script)); await Run("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {e}"); }
    async Task<string> PSC(string script) { var e = Convert.ToBase64String(Encoding.Unicode.GetBytes(script)); return await Capture("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {e}"); }
    async Task Run(string f, string a) { var (_, err, c) = await Exec(f, a); if (c != 0 && !string.IsNullOrWhiteSpace(err)) throw new Exception(err.Trim()); }
    async Task<string> Capture(string f, string a) { var (o, err, c) = await Exec(f, a); if (c != 0 && !string.IsNullOrWhiteSpace(err)) throw new Exception(err.Trim()); return o; }

    static async Task<(string, string, int)> Exec(string f, string a)
    {
        var psi = new ProcessStartInfo(f, a) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de lancer " + f);
        var ot = p.StandardOutput.ReadToEndAsync();
        var et = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await ot, await et, p.ExitCode);
    }
}