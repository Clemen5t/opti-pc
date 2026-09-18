# Opti-PC v0.3

Optimiseur Windows 11 local, mesurable et réversible pour PC gaming AMD, ciblé en priorité sur Ryzen 7 7800X3D + Radeon RX 7900 XT + MSI B650 Gaming Plus WiFi.

## OPTIMISER TOUT
- benchmark réseau avant/après : passerelle + Cloudflare + Google
- ping, jitter, pertes et score comparatif
- sauvegarde complète + point de restauration Windows
- Mode Jeu + HAGS
- capture Game DVR en arrière-plan coupée en Auto/Compétitif
- plan Équilibré pour Ryzen X3D
- RSS activé et TCP Auto-Tuning Normal
- RSC testé automatiquement A/B puis meilleur état conservé
- économie d'énergie NIC réduite si le pilote le permet
- Realtek RTL8125BG : EEE / Green Ethernet / Gigabit Lite désactivés seulement si les propriétés existent et proposent Disabled
- cache DNS vidé sans imposer de DNS tiers
- audit pilotes GPU/chipset/réseau et logiciels au démarrage
- rapport : %ProgramData%\OptiPC\last-report.txt
- rollback : %ProgramData%\OptiPC\backup.json

## Profils
- Auto recommandé
- Compétitif / latence minimale
- Équilibré / stabilité

## Sécurité
Defender, pare-feu, Windows Update, IPv6 et mitigations restent actifs. Pas de tweaks HPET/timer/Nagle/NetworkThrottlingIndex. Aucun overclock/undervolt automatique. PBO, Curve Optimizer, EXPO et BIOS restent manuels avec tests de stabilité.

## EXE
GitHub Actions compile un EXE Windows x64 autonome : **Actions → build → artifact OptiPC-win-x64**.
