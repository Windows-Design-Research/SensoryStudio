# Build and run Sensory Studio

Follow these steps to run **Zen Garden** and **Power Orb** on your Windows PC. You don't need Visual Studio or prior experience with the app.

## 1. Install the build tools

Install the **[.NET 10 SDK for Windows](https://dotnet.microsoft.com/download/dotnet/10.0)**. Choose the **SDK**, not just the Runtime.

Once installation finishes, open a new PowerShell window and run:

```powershell
dotnet --version
```

You should see a version number beginning with `10.`.

For the full experience, use hardware that supports advanced haptics on Windows. Haptic feedback also requires a compatible Windows version. You can still use the visuals and available sound without haptic hardware.

## 2. Download the project

1. Open the [Sensory Studio repository](https://github.com/Windows-Design-Research/SensoryStudio).
2. Select **Code > Download ZIP**.
3. Extract the ZIP to a folder on your PC.
4. Open the extracted folder containing **`SensoryStudio.sln`**.
5. Right-click an empty area in that folder and select **Open in Terminal**.

Alternatively, if you already use Git:

```powershell
git clone https://github.com/Windows-Design-Research/SensoryStudio.git
cd SensoryStudio
```

## 3. Build for your PC

Check **Settings > System > About > System type**:

| Your PC | Build to use |
|---|---|
| x64-based processor, typically Intel or AMD | **x64** |
| ARM-based processor, such as Snapdragon | **ARM64** |

Follow **only the section matching your PC** below, running the commands from the project folder.

First, restore (download) the required packages. These commands use NuGet.org's official v2 HTTPS endpoint to avoid the v3 connection failure reported on some networks. You'll need an internet connection; TLS verification remains enabled.

### x64

Restore packages:

```powershell
dotnet restore .\SensoryStudio.sln -p:Configuration=Release -p:Platform=x64 -p:SelfContained=true --source https://www.nuget.org/api/v2/
```

Only after restore succeeds, build:

```powershell
dotnet build .\SensoryStudio.sln -c Release -p:Platform=x64 --self-contained true --no-restore
```

### ARM64

Restore packages:

```powershell
dotnet restore .\SensoryStudio.sln -p:Configuration=Release -p:Platform=ARM64 -p:SelfContained=true --source https://www.nuget.org/api/v2/
```

Only after restore succeeds, build:

```powershell
dotnet build .\SensoryStudio.sln -c Release -p:Platform=ARM64 --self-contained true --no-restore
```

Keep `--no-restore` on the build command so it uses the downloaded packages instead of retrying the project's default v3 endpoint. Wait for **Build succeeded** before continuing.

**Package security checks:** A successful restore or build through v2 does not establish that package vulnerability checks passed. If you see `NU1900`, NuGet could not retrieve vulnerability information. See [package vulnerability checks](#package-vulnerability-checks) below.

## 4. Open the app

From the project folder, open the folder matching your build:

**x64**

```text
src\SensoryStudio.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0
```

**ARM64**

```text
src\SensoryStudio.WinUI\bin\ARM64\Release\net8.0-windows10.0.19041.0
```

Double-click **`SensoryStudio.WinUI.exe`**. If Windows hides file extensions, look for **SensoryStudio.WinUI** with the type **Application**.

Choose **Zen Garden** or **Power Orb** from the home screen.

In Power Orb, select **Pop out mode** <img src="pop-out-icon.png" alt="Pop out mode icon" width="20" height="20"> to keep the orb in a separate transparent window for digital fidgeting during meetings or other focused tasks.

Keep the executable in its output folder - the app needs the files beside it. There's no separate installer to run.

## Troubleshooting

### "dotnet" is not recognized

Make sure you installed the **.NET SDK**, then close and reopen Terminal.

### The build cannot download packages

Check your internet connection and whether your network allows access to **NuGet.org** and its package downloads, then retry the restore command in step 3. Only build after restore succeeds.

### NU1301: SSL connection or "HandshakeFailure"

This means the build could not establish a secure connection to NuGet to download packages. It happens before app compilation. A proxy, VPN, network filter, or local TLS configuration can cause it; the error alone doesn't identify which one.

If the error names `https://api.nuget.org/v3/index.json`, make sure you used the restore command in step 3 with its `--source` option and kept `--no-restore` on the subsequent build command.

The v2 endpoint still requires secure access to NuGet's package downloads and may not work on every network. If it also fails, contact your IT or network support team with the error details. Do not disable certificate validation or security software.

### Package vulnerability checks

The alternative package endpoint is not a substitute for vulnerability auditing. Once access to NuGet's v3 endpoint is working, rerun your restore command from step 3 without the `--source` option to use the project's normal source and review any audit warnings. Do not suppress `NU1900` or disable auditing to treat a build as security-cleared.

### Zen Garden won't load

Make sure the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) is installed. It's normally already available on Windows 11.

### There's no haptic feedback

Check that haptics are enabled in the app and that your device and Windows version support advanced haptics.

### Windows says the app cannot run on this PC

Check that you built and opened the version matching your PC's system type. An ARM64 build will not run on an x64 PC.
