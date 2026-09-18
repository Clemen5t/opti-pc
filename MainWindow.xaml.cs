using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace OptiPC;

public partial class MainWindow : Window
{
    readonly string dataDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"OptiPC");
    string BackupPath=>Path.Combine(dataDir,"backup.json");
    string ReportPath=>Path.Combine(dataDir,"last-report.txt");

    public MainWindow(){InitializeComponent();Directory.CreateDirectory(dataDir);Loaded+=async(_,_)=>{AdminText.Text=IsAdmin()?"Administrateur ✓":"Administrateur requis";await AnalyzeAsync();};}
    bool IsAdmin()=>new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    void Log(string s){LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");LogBox.ScrollToEnd();}
    void Status(string s,double p){StatusText.Text=s;Progress.Value=Math.Clamp(p,0,100);}
    private async void Analyze_Click(object s,RoutedEventArgs e)=>await AnalyzeAsync();
    private async void Benchmark_Click(object s,RoutedEventArgs e)=>await BenchmarkAsync("MANUEL");

    async Task AnalyzeAsync(){
      Status("Analyse complète...",10);
      try{
        var cpu=Wmi("Win32_Processor","Name");var gpu=Wmi("Win32_VideoController","Name");
        var board=Wmi("Win32_BaseBoard","Product");var bios=Wmi("Win32_BIOS","SMBIOSBIOSVersion");
        var os=Wmi("Win32_OperatingSystem","Caption");var build=Wmi("Win32_OperatingSystem","BuildNumber");
        var ram=Wmi("Win32_ComputerSystem","TotalPhysicalMemory",true);
        HardwareText.Text=$"CPU : {cpu}\nGPU : {gpu}\nRAM : {ram}\nCarte mère : {board}\nBIOS : {bios}";
        var hags=Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers","HwSchMode",null);
        var game=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar","AutoGameModeEnabled",null);
        WindowsText.Text=$"{os} (build {build})\nGame Mode : {RegState(game,1)}\nHAGS : {RegState(hags,2)}\nÉnergie : {Clean(await Capture("powercfg","/getactivescheme"))}";
        Status("Analyse réseau...",45);
        var nic=PrimaryNic();
        if(nic!=null){
          var ip=nic.GetIPProperties();var gw=ip.GatewayAddresses.FirstOrDefault()?.Address?.ToString()??"aucune";
          var dns=string.Join(", ",ip.DnsAddresses.Select(x=>x.ToString()));var q=PQ(nic.Name);
          var d=await PSC($"Get-NetAdapter -Name {q}|Select Name,InterfaceDescription,DriverVersion,DriverDate,LinkSpeed|Format-List|Out-String");
          var rss=await PSC($"Get-NetAdapterRss -Name {q} -ErrorAction SilentlyContinue|Select Enabled|Format-List|Out-String");
          var rsc=await PSC($"Get-NetAdapterRsc -Name {q} -ErrorAction SilentlyContinue|Select IPv4Enabled,IPv6Enabled|Format-List|Out-String");
          NetworkText.Text=$"{nic.Name} ({nic.NetworkInterfaceType})\nLien : {nic.Speed/1_000_000d:0} Mb/s\nPasserelle : {gw}\nDNS : {dns}\n{Clean(d)}\n{Clean(rss)}\n{Clean(rsc)}";
        } else NetworkText.Text="Aucune interface active détectée.";
        DriversText.Text=$"Pilotes graphiques :\n{VideoDrivers()}\n\nChipset AMD détecté :\n{Clean(await PSC(@"Get-CimInstance Win32_PnPSignedDriver|Where-Object {$_.DeviceName -match 'AMD.*(SMBus|GPIO|PSP|PCI)'}|Sort DriverDate -Descending|Select -First 6 DeviceName,DriverVersion,DriverDate|Format-Table -AutoSize|Out-String"))}";
        StartupBox.Text=await PSC(@"Get-CimInstance Win32_StartupCommand|Select Name,Location,Command|Sort Name|Format-Table -Wrap -AutoSize|Out-String");
        Log("Analyse complète terminée.");Status("Prêt.",100);
      }catch(Exception ex){Log("ERREUR analyse : "+ex.Message);Status("Erreur d'analyse.",0);}
    }

    private async void Optimize_Click(object s,RoutedEventArgs e){
      if(!IsAdmin()){MessageBox.Show("Relance Opti-PC en administrateur.");return;}
      try{
        Status("Benchmark AVANT...",5);var before=await BenchmarkAsync("AVANT");
        Status("Sauvegarde...",15);await BackupAsync();
        await PS(@"try{Enable-ComputerRestore -Drive ($env:SystemDrive+'\') -ErrorAction SilentlyContinue;Checkpoint-Computer -Description 'Opti-PC avant optimisation' -RestorePointType MODIFY_SETTINGS -ErrorAction SilentlyContinue}catch{}");
        Log("Point de restauration demandé si Protection du système disponible.");
        Status("Windows Gaming...",30);
        await PS(@"New-Item 'HKCU:\Software\Microsoft\GameBar' -Force|Out-Null;Set-ItemProperty 'HKCU:\Software\Microsoft\GameBar' AllowAutoGameMode 1 -Type DWord -Force;Set-ItemProperty 'HKCU:\Software\Microsoft\GameBar' AutoGameModeEnabled 1 -Type DWord -Force");
        await PS(@"New-Item 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Force|Out-Null;Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' HwSchMode 2 -Type DWord -Force");
        await Run("powercfg","/setactive SCHEME_BALANCED");Log("Game Mode + HAGS demandés; plan Équilibré actif.");
        Status("Réseau faible latence...",50);
        await Run("netsh","int tcp set global rss=enabled");await Run("netsh","int tcp set global autotuninglevel=normal");
        var nic=PrimaryNic();
        if(nic!=null){
          var q=PQ(nic.Name);
          await PS($"try{{Enable-NetAdapterRss -Name {q} -ErrorAction Stop}}catch{{}}");
          await PS($"try{{Disable-NetAdapterRsc -Name {q} -IPv4 -IPv6 -ErrorAction Stop}}catch{{}}");
          await PS($"try{{Set-NetAdapterPowerManagement -Name {q} -AllowComputerToTurnOffDevice Disabled -ErrorAction Stop}}catch{{}}");
          await PS($"$p=Get-NetAdapterAdvancedProperty -Name {q} -ErrorAction SilentlyContinue|Where-Object {{$_.DisplayName -match 'Energy.Efficient|Green Ethernet|Économie.*énergie'}};foreach($x in $p){{try{{Set-NetAdapterAdvancedProperty -Name {q} -RegistryKeyword $x.RegistryKeyword -RegistryValue 0 -NoRestart -ErrorAction Stop}}catch{{}}}}");
          Log($"RSS activé, RSC désactivé, économie NIC réduite sur {nic.Name} quand supporté.");
        }
        await Run("ipconfig","/flushdns");Log("TCP Auto-Tuning = Normal; DNS non remplacé; cache DNS vidé.");
        Status("Benchmark APRÈS...",75);var after=await BenchmarkAsync("APRÈS");
        ProfileText.Text="Gaming / faible latence : Game Mode + HAGS + RSS + RSC off sur interface active + économie NIC réduite. Defender, pare-feu, Windows Update, IPv6, PBO/CO restent intacts.";
        await File.WriteAllTextAsync(ReportPath,$"Opti-PC {DateTime.Now:yyyy-MM-dd HH:mm}\n\nAVANT\n{before}\nAPRÈS\n{after}");
        await AnalyzeAsync();Status("Terminé — redémarrage conseillé.",100);MessageBox.Show("Optimisation terminée. Redémarre Windows pour finaliser HAGS. La sauvegarde permet le retour arrière.","Opti-PC");
      }catch(Exception ex){Log("ERREUR optimisation : "+ex.Message);Status("Optimisation interrompue.",0);}
    }

    async Task BackupAsync(){
      var ps=@"$b=[ordered]@{};$b.Power=(powercfg /getactivescheme|Out-String);$b.HwSchMode=(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name HwSchMode -ErrorAction SilentlyContinue).HwSchMode;$b.Game=(Get-ItemProperty 'HKCU:\Software\Microsoft\GameBar' -ErrorAction SilentlyContinue|Select AllowAutoGameMode,AutoGameModeEnabled);$b.Adapters=@();Get-NetAdapter -Physical -ErrorAction SilentlyContinue|%{$n=$_.Name;$b.Adapters+=[ordered]@{Name=$n;Rss=(Get-NetAdapterRss -Name $n -ErrorAction SilentlyContinue|Select Enabled);Rsc=(Get-NetAdapterRsc -Name $n -ErrorAction SilentlyContinue|Select IPv4Enabled,IPv6Enabled);Advanced=(Get-NetAdapterAdvancedProperty -Name $n -ErrorAction SilentlyContinue|Select RegistryKeyword,RegistryValue)}};$b|ConvertTo-Json -Depth 10";
      await File.WriteAllTextAsync(BackupPath,await PSC(ps));Log("Sauvegarde créée : "+BackupPath);
    }

    private async void Restore_Click(object s,RoutedEventArgs e){
      if(!File.Exists(BackupPath)){MessageBox.Show("Aucune sauvegarde Opti-PC trouvée.");return;}
      try{
        Status("Restauration...",20);using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(BackupPath));var root=doc.RootElement;
        if(root.TryGetProperty("Power",out var p)){var m=Regex.Match(p.GetString()??"",@"[0-9a-fA-F-]{36}");if(m.Success)await Run("powercfg","/setactive "+m.Value);}
        if(root.TryGetProperty("HwSchMode",out var h)&&h.ValueKind==JsonValueKind.Number)await PS($"Set-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers' HwSchMode {h.GetInt32()} -Type DWord -Force");
        else await PS(@"Remove-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' HwSchMode -ErrorAction SilentlyContinue");
        if(root.TryGetProperty("Game",out var g)&&g.ValueKind==JsonValueKind.Object)foreach(var k in new[]{"AllowAutoGameMode","AutoGameModeEnabled"})if(g.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Number)await PS($"New-Item 'HKCU:\\Software\\Microsoft\\GameBar' -Force|Out-Null;Set-ItemProperty 'HKCU:\\Software\\Microsoft\\GameBar' {k} {v.GetInt32()} -Type DWord -Force");
        if(root.TryGetProperty("Adapters",out var aa)&&aa.ValueKind==JsonValueKind.Array)foreach(var a in aa.EnumerateArray()){
          if(!a.TryGetProperty("Name",out var ne))continue;var q=PQ(ne.GetString()??"");
          if(a.TryGetProperty("Rss",out var rss)&&rss.ValueKind==JsonValueKind.Object&&rss.TryGetProperty("Enabled",out var re)&&(re.ValueKind==JsonValueKind.True||re.ValueKind==JsonValueKind.False))await PS(re.GetBoolean()?$"try{{Enable-NetAdapterRss -Name {q} -ErrorAction Stop}}catch{{}}":$"try{{Disable-NetAdapterRss -Name {q} -ErrorAction Stop}}catch{{}}");
          if(a.TryGetProperty("Rsc",out var rsc)&&rsc.ValueKind==JsonValueKind.Object){bool v4=rsc.TryGetProperty("IPv4Enabled",out var x4)&&x4.ValueKind==JsonValueKind.True;bool v6=rsc.TryGetProperty("IPv6Enabled",out var x6)&&x6.ValueKind==JsonValueKind.True;await PS($"try{{Set-NetAdapterRsc -Name {q} -IPv4Enabled $"+v4.ToString().ToLower()+" -IPv6Enabled $"+v6.ToString().ToLower()+" -ErrorAction Stop}}catch{}");}
          if(a.TryGetProperty("Advanced",out var adv)&&adv.ValueKind==JsonValueKind.Array)foreach(var x in adv.EnumerateArray()){if(!x.TryGetProperty("RegistryKeyword",out var ke)||!x.TryGetProperty("RegistryValue",out var rv))continue;var key=ke.GetString()??"";if(key.Length==0)continue;var val=rv.ValueKind==JsonValueKind.Array?string.Join(",",rv.EnumerateArray().Select(z=>z.ToString())):rv.ToString();await PS($"try{{Set-NetAdapterAdvancedProperty -Name {q} -RegistryKeyword {PQ(key)} -RegistryValue {PQ(val)} -NoRestart -ErrorAction Stop}}catch{{}}");}
        }
        Log("Paramètres sauvegardés restaurés.");ProfileText.Text="Restauré depuis la sauvegarde";Status("Restauration terminée.",100);
      }catch(Exception ex){Log("ERREUR restauration : "+ex.Message);Status("Erreur restauration.",0);}
    }

    async Task<string> BenchmarkAsync(string label){
      BenchmarkBox.AppendText($"===== {label} — {DateTime.Now:HH:mm:ss} =====\n");var t=new List<string>();var nic=PrimaryNic();var gw=nic?.GetIPProperties().GatewayAddresses.FirstOrDefault()?.Address?.ToString();if(!string.IsNullOrWhiteSpace(gw))t.Add(gw);t.AddRange(new[]{"1.1.1.1","8.8.8.8"});var sb=new StringBuilder();
      foreach(var target in t.Distinct()){var r=await PingStats(target,20);var line=$"{target,-16} moyenne {r.avg,6:0.0} ms | min {r.min,4} | max {r.max,4} | jitter {r.jitter,5:0.0} ms | pertes {r.loss,5:0.0}%";sb.AppendLine(line);BenchmarkBox.AppendText(line+"\n");}
      var sw=Stopwatch.StartNew();try{await Dns.GetHostAddressesAsync("www.microsoft.com");}catch{}sw.Stop();var dl=$"DNS indicatif : {sw.Elapsed.TotalMilliseconds:0.0} ms (ce n'est pas le ping en jeu).";sb.AppendLine(dl);BenchmarkBox.AppendText(dl+"\n\n");BenchmarkBox.ScrollToEnd();return sb.ToString();
    }
    static async Task<(double avg,long min,long max,double jitter,double loss)> PingStats(string target,int count){var v=new List<long>();using var p=new Ping();for(int i=0;i<count;i++){try{var r=await p.SendPingAsync(target,1500);if(r.Status==IPStatus.Success)v.Add(r.RoundtripTime);}catch{}await Task.Delay(80);}if(v.Count==0)return(0,0,0,0,100);var j=v.Count<2?0:v.Zip(v.Skip(1),(a,b)=>Math.Abs(a-b)).Average();return(v.Average(),v.Min(),v.Max(),j,100d*(count-v.Count)/count);}

    private async void Drivers_Click(object s,RoutedEventArgs e){await AnalyzeAsync();MessageBox.Show("Versions installées actualisées.\n\nRX 7900 XT : privilégie le pilote AMD WHQL Recommended.\nChipset AM5 : package AMD officiel.\nCarte mère : BIOS/réseau depuis MSI.\n\nOpti-PC n'installe pas automatiquement BIOS ou pilote optionnel : une mauvaise version peut rendre le PC instable.","Pilotes / BIOS");}

    NetworkInterface? PrimaryNic()=>NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback&&n.GetIPProperties().GatewayAddresses.Any()).OrderByDescending(n=>n.Speed).FirstOrDefault();
    string Wmi(string c,string p,bool gb=false){using var s=new ManagementObjectSearcher($"SELECT {p} FROM {c}");foreach(ManagementObject o in s.Get()){var v=o[p];if(v==null)continue;if(gb&&ulong.TryParse(v.ToString(),out var b))return $"{b/1073741824d:0.0} Go";return v.ToString()??"Inconnu";}return"Inconnu";}
    string VideoDrivers(){using var s=new ManagementObjectSearcher("SELECT Name,DriverVersion,DriverDate FROM Win32_VideoController");return string.Join("\n",s.Get().Cast<ManagementObject>().Select(o=>$"{o["Name"]} — {o["DriverVersion"]} — {o["DriverDate"]}"));}
    static string PQ(string s)=>"'"+s.Replace("'","''")+"'";static string Clean(string s)=>Regex.Replace(s??"",@"\r?\n\s*\r?\n","\n").Trim();static string RegState(object? v,int on)=>v is int i?(i==on?"Activé":$"Valeur {i}"):"Non défini";
    async Task PS(string script){var e=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));await Run("powershell.exe",$"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {e}");}
    async Task<string> PSC(string script){var e=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));return await Capture("powershell.exe",$"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {e}");}
    async Task Run(string f,string a){var(_,err,c)=await Exec(f,a);if(c!=0&&!string.IsNullOrWhiteSpace(err))throw new Exception(err.Trim());}
    async Task<string> Capture(string f,string a){var(o,err,c)=await Exec(f,a);if(c!=0&&!string.IsNullOrWhiteSpace(err))throw new Exception(err.Trim());return o;}
    static async Task<(string,string,int)> Exec(string f,string a){var psi=new ProcessStartInfo(f,a){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};using var p=Process.Start(psi)??throw new InvalidOperationException("Impossible de lancer "+f);var ot=p.StandardOutput.ReadToEndAsync();var et=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();return(await ot,await et,p.ExitCode);}
}