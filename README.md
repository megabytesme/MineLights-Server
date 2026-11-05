# MineLights

| Logo | Video Example |
|----------|-------|
| <img src="https://github.com/user-attachments/assets/c0d50adb-0242-4603-89a5-074b40903606" alt="MineLights Icon" height="200"> | ![output](https://github.com/user-attachments/assets/fd0adbbf-eaf4-46c1-bcfc-e56b5df13e32) |
| Made by me! (I love MS Paint) | <small><em>[MineLights for Minecraft](https://github.com/megabytesme/MineLights) running on an Asus laptop, making use of MineLights Server - Controlling Asus Aura Sync hardware. Video courtesy of Nukepatrol99.</em></small> |

MineLights Server is a .NET app with modular design to support a wide range of hardware.

## Features

- **Extensive Multi-SDK Support**: Integrates with a wide array of RGB SDKs simultaneously, including Corsair iCUE, Logitech G HUB, Razer Chroma, MSI Mystic Light, ASUS Aura, SteelSeries GameSense, and Wooting.
- **Highly Configurable**: The APIs lets you enable/disable every feature, integration, and even individual devices.

## Supported RGB Software

- **Corsair iCUE**: Full device support for keyboards, mice, headsets, and more via the iCUE SDK (requires Corsair iCUE - Windows).
- **Logitech G HUB / Lightsync**: Controls Logitech G keyboards, mice, headsets, and other Lightsync-enabled gear (requires Logitech G HUB - Windows).
- **Razer Chroma**: Extensive support for all Razer Chroma-enabled peripherals like keyboards, mice, and mousepads (requires Razer Synapse - Windows).
- **MSI Mystic Light**: Control for motherboards, GPUs, and other devices via the Mystic Light SDK (requires MSI Center - Windows).
- **ASUS Aura Sync**: Control for motherboards, GPUs, and other devices via the ASUS Aura SDK (requires Armoury Crate - Windows).
- **SteelSeries GameSense**: Integration with SteelSeries peripherals like keyboards, mice, and headsets (requires SteelSeries GG - Windows).
- **Wooting**: Direct, low-latency control for Wooting analog keyboards (requires Wootility software to be running - Windows).
- **Novation**: Support for Novation MIDI controllers like the Launchpad, enabling unique grid-based effects (Windows).
- **Raspberry Pi Pico**: Directly control custom DIY lighting projects powered by a Raspberry Pi Pico (Windows).

### Building from source

The RGB.NET source is pinned as a Git submodule. Clone with `git clone --recursive`, or run `git submodule update --init --recursive` after cloning, then build with the .NET 10 SDK:

```powershell
dotnet build MineLights.csproj -c Release -m /p:GeneratePackageOnBuild=false
```

RGB.NET is provided under its own licence in `third_party/RGB.NET/LICENSE`.

## 🛠️ Installation

### Prerequisites

For the MineLights Server to function correctly, please ensure the following requirements are met.

*   **.NET 10 SDK (building from source):** The published Windows executable is self-contained and does not require a separate .NET installation.
*   **Hardware SDK Software:** For MineLights to control your hardware, the official manufacturer's software and services often need to be running. This includes:
    *   **Corsair:** iCUE (version 4 or newer is recommended).
    *   **Asus:** Aura Sync / Armoury Crate.
    *   **Logitech:** Logitech G HUB.
    *   **Razer:** Razer Synapse.
    *   *Other brands will have similar requirements.*
*   **Administrator Privileges (Optional):** Some SDKs, like MSI Mystic Light, may require the server to be run as an administrator to function correctly.

#### Network

*   **Local V2 connection:** MineLights Server listens on an OS-selected TCP port bound to loopback and writes a protected endpoint document under `%LOCALAPPDATA%\MineLights\v2\`. Updated clients use that endpoint for TLS and automatic local enrollment after the user authorizes the server to run.
*   **Remote V2 connection:** Remote access is off by default. Enable it from the server tray menu, choose the network interfaces and port, then export a one-time `.mlpair` file for the client. Pairing files expire after five minutes and must be transferred securely. MineLights Server does not configure router port forwarding.
*   **Legacy V1 clients:** Unauthenticated legacy lighting is off by default. The tray menu can enable it for loopback clients only. It does not expose V1 process-management commands. Because V1 has no reliable disconnect signal, V2 shutdown requests are refused while legacy access is enabled; disable legacy access or exit from the tray when ready to stop the server.

---

### Installation

- Simply download and run the server executable. It is self-contained, and on startup will make a ```config.json``` file in the same directory.

---

## Usage

### V2 Connections

V2 uses one TLS TCP session for authentication, device discovery, requests, and lighting frames. Local clients discover the endpoint through the protected `endpoint.json` file, then enroll with the local key after the user permits MineLights Server to run. The local port is chosen by the OS; the remote listener uses the port selected in the tray settings.

The server tray menu provides owner controls for legacy V1 access, remote interface and port selection, pairing-file export, and revoking paired clients. Remote access is authenticated and requires an explicit one-time pairing file. An authenticated V2 client may request shutdown, which is refused if another authenticated session is connected or legacy access is enabled. Restart and configuration remain tray controls.

### Legacy V1 endpoints (only when explicitly enabled)

Older clients use fixed loopback endpoints without authentication. This compatibility mode is disabled by default and can be enabled from the tray menu. It never enables the old raw command endpoint. Each legacy handshake filters its device list by that client's enabled integrations and disabled device IDs without changing the server owner's settings or other clients' devices. The server owner must also enable a provider in `config.json` for its devices to be available.

---

#### 1. Handshake Endpoint

This endpoint lets a legacy client request the current device layout. The server ignores client hardware-configuration fields; integrations remain server-owner settings.

*   **Protocol:** `TCP`
*   **Port:** `63211`
*   **Direction:** Bidirectional

##### Client -> Server Payload

The client initiates the connection and sends a JSON object prefixed with a 32-bit integer representing the payload length.

**Structure:**
```json
{
  "enabled_integrations": ["Corsair", "Logitech", "Asus", "Razer"],
  "disabled_devices": ["Corsair|MM800 RGB POLARIS"]
}
```
*   **`enabled_integrations`** and **`disabled_devices`**: Legacy client settings, ignored by the server. Configure server integrations in the server's own `config.json`.

##### Server -> Client Response

The server sends back the current device layout as a JSON object prefixed with its length. The client configuration in its request is ignored.

**Structure:**
```json
{
  "devices": [
    {
      "sdk": "Corsair",
      "name": "K100 RGB",
      "leds": [0, 1, 2, 3, ...],
      "key_map": {
        "LCTRL": 0,
        "MINUS": 1,
        "Y": 2,
        "...": "..."
      }
    },
    {
      "sdk": "Corsair",
      "name": "DRAM",
      "leds": [174, 175, 176, ...],
      "key_map": {
        "DRAM1": 174,
        "DRAM2": 175,
        "...": "..."
      }
    }
  ]
}
```
*   **`devices`** (Array of Objects): A list of all hardware devices found by the server.
    *   **`sdk`** (String): The manufacturer or SDK provider (e.g., "Corsair").
    *   **`name`** (String): The model name of the device (e.g., "K100 RGB").
    *   **`leds`** (Array of Integers): A list of all unique global LED IDs belonging to this device.
    *   **`key_map`** (Object): A map where keys are the standardized friendly names (e.g., "LCTRL", "DRAM1") and values are the corresponding global LED IDs for that specific device.

---

#### 2. Lighting Data Endpoint

This endpoint is used for high-frequency updates of LED colors. The client sends lighting data every frame.

*   **Protocol:** `UDP`
*   **Port:** `63212`
*   **Direction:** Client -> Server

##### Client -> Server Payload

A JSON object containing an array of color updates.

**Structure:**
```json
{
  "led_colors": [
    {
      "id": 0,
      "r": 255,
      "g": 0,
      "b": 0
    },
    {
      "id": 1,
      "r": 0,
      "g": 255,
      "b": 0
    }
  ]
}
```
*   **`led_colors`** (Array of Objects): A list of LEDs to update.
    *   **`id`** (Integer): The global LED ID to change.
    *   **`r`, `g`, `b`** (Integer): The RGB color values (0-255).

---

#### 3. Command Endpoint

TCP `63213` remains bound to loopback as a presence probe so older clients can detect the server and continue to their legacy handshake. The listener closes every connection immediately without reading payloads; V1 commands are never executed. The tray menu and authenticated V2 shutdown requests manage server lifetime.

---

#### 4. Discovery Endpoint

When legacy compatibility is enabled, the server uses this endpoint to announce itself on the local network for older clients.

*   **Protocol:** `UDP Broadcast`
*   **Port:** `63214`
*   **Direction:** Server -> Client

##### Server -> Client Payload

A raw UTF-8 string is broadcasted to all devices on the local network every 5 seconds.

**Message:**
```MINELIGHTS_PROXY_HELLO```

The Minecraft client listens on this port for this specific message. Upon receiving it, the client knows the server is running and can initiate the **Handshake** process.

---

## Configuration

The server's behavior can be configured by editing the `config.json` file located in the same directory as `MineLights.exe`. If the file does not exist, the server uses default values until a setting is saved.

**Example `config.json`:**
```json
{
  "EnabledIntegrations": [
    "Corsair",
    "Logitech",
    "Asus",
    "Razer",
    "Wooting",
    "SteelSeries",
    "Msi",
    "Novation",
    "PicoPi"
  ],
  "DisabledDevices": [
    "Corsair|MM800 RGB POLARIS"
  ]
}
```

*   **`EnabledIntegrations`**: A list of hardware brands the server will try to initialize. New configurations include all nine supported providers. Existing lists are preserved; add `Novation` or `PicoPi` to an older list if needed. An empty list disables all providers. You can remove entries to prevent the server from hooking into certain SDKs, which can sometimes improve stability or resolve conflicts.

*   **`DisabledDevices`**: A list of specific devices to ignore. This is useful if a particular piece of hardware is causing issues but you still want to use other devices from the same brand. The format must be `"Manufacturer|Model"`, which you can find in the server's log files upon startup.

Older server versions saved these global filters from whichever legacy client last connected. On upgrade, MineLights Server asks the owner to review them: choose to keep the existing filters or explicitly enable all providers and clear the server-wide disabled-device list. The **Server Connections → Review device filters** tray option is always available. Resetting these server-wide filters may interrupt lighting while devices are refreshed, and does not change individual clients' settings.

Device snapshots made by earlier versions may not identify their provider. The server recognizes common manufacturer names and reports unknown providers in its log. If an integration-filtered client cannot see such an emulated device, create a fresh snapshot with the current server or add its correct `Provider` value to that device in the snapshot. Supported values are `Corsair`, `Logitech`, `Asus`, `Razer`, `Wooting`, `SteelSeries`, `Msi`, `Novation`, and `PicoPi`.

---
## Troubleshooting

If you encounter issues, follow these steps:

1.  **Check the Server Log:** The first and most important step is to check the `minelights-server.log` file (or the console output). It provides detailed information on which SDKs were initialized and which devices were mapped. Look for any `WARNING` or `ERROR` messages.

3.  **Hardware Not Detected by Server:**
    *   Ensure the corresponding manufacturer's software (iCUE, G HUB, Synapse) is running in the background.
    *   Try running `MineLights.exe` as an administrator, as some SDKs require it.
    *   Verify that your device is not listed in the `DisabledDevices` section of `config.json`.

4.  **No Connection Between Client and Server:**
    *   Make sure Minecraft and `MineLights.exe` are running on the same computer.
    *   If the server is running but MineLights does not connect, allow the Minecraft Java process to receive the UDP `63214` discovery message on private networks.


## Roadmap

- **Windows Dynamic Lighting**: Integration with Windows Dynamic Lighting is planned for the future.
- **Improve Device Compatibility**: Continuously working to improve support for more devices across all SDKs.
- **Community Testing**: Ideally have testers, well test!

## Contributing

Contributions are welcome! Please fork the repository and submit a pull request.

## Contact

For any questions, suggestions, or bug reports, please [open an issue](https://github.com/megabytesme/MineLights-Server/issues) on the GitHub repository.
