# Opti-PC v0.2

Optimiseur Windows 11 local, mesurable et réversible pour Ryzen 7 7800X3D + Radeon RX 7900 XT, avec prise en charge du Realtek 8125BG 2.5 GbE de la MSI B650 Gaming Plus WiFi.

## OPTIMISER TOUT
- benchmark réseau avant/après : passerelle + 1.1.1.1 + 8.8.8.8, ping/jitter/pertes
- sauvegarde des paramètres et demande d'un point de restauration Windows
- Game Mode + HAGS
- plan Équilibré (pas d'Ultimate Performance forcé sur X3D)
- RSS activé ; TCP Auto-Tuning conservé sur Normal
- RSC désactivé sur l'interface active pour le profil faible latence
- économie d'énergie NIC réduite si supportée
- EEE / Green Ethernet désactivé uniquement si le pilote expose réellement le réglage
- cache DNS vidé, sans imposer de DNS tiers
- analyse pilotes GPU/chipset/réseau, BIOS, logiciels au démarrage
- rapport dans `%ProgramData%\OptiPC\last-report.txt`
- restauration depuis `%ProgramData%\OptiPC\backup.json`

## Ce qui n'est volontairement pas automatisé
Defender, pare-feu, Windows Update, IPv6 et mitigations restent actifs. Pas de tweaks HPET/timer/Nagle/NetworkThrottlingIndex. Aucun overclock/undervolt automatique. PBO, Curve Optimizer, EXPO, BIOS et pilotes optionnels demandent une validation et des tests de stabilité.

## EXE
GitHub Actions construit un EXE Windows x64 autonome. Ouvre **Actions > build** puis télécharge l'artifact **OptiPC-win-x64**.