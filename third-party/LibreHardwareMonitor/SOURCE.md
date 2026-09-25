# LibreHardwareMonitor 0.9.6

Unmodified NuGet library used only by Secondary.SensorHelper (optional, separate process).

Source and covered-file licenses: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6

Release: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.6

License: MPL-2.0; see LICENSE and THIRD-PARTY-NOTICES.txt in this folder.

Exact NuGet dependency versions and content hashes: src/Secondary.SensorHelper/packages.lock.json in the Secondary source tree. No PawnIO installer, driver package or LibreHardwareMonitor GUI is bundled or launched. Hardware reads use available vendor APIs and, only when already installed, PawnIO; unsupported/denied sensors are unavailable. CPU and Intel GPU probing are skipped when PawnIO is absent.
