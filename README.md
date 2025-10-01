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

### This project uses the RGB.Net Nuget package.

## 🛠️ Installation

### Prerequisites

For the MineLights Server to function correctly, please ensure the following requirements are met.

*   **.NET 9.0 (or newer) Desktop Runtime:** The server application is built on .NET and requires the runtime to be installed. You can download it from [Microsoft's official website](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
*   **Hardware SDK Software:** For MineLights to control your hardware, the official manufacturer's software and services often need to be running. This includes:
    *   **Corsair:** iCUE (version 4 or newer is recommended).
    *   **Asus:** Aura Sync / Armoury Crate.
    *   **Logitech:** Logitech G HUB.
    *   **Razer:** Razer Synapse.
    *   *Other brands will have similar requirements.*
*   **Administrator Privileges (Optional):** Some SDKs, like MSI Mystic Light, may require the server to be run as an administrator to function correctly.

#### Network

*   **Local Network Access:** The client and the server (`MineLights.exe`) must be running on the same local network.
*   **Firewall Configuration:** Ensure your firewall (e.g., Windows Defender Firewall) allows communication on the TCP/UDP ports listed above (`63211`, `63212`, `63213`, `63214`). A firewall prompt may appear on the first run of `MineLights.exe`; be sure to "Allow access".

---

### Installation

- Simply download and run the server executable. It is self-contained, and on startup will make a ```config.json``` file in the same directory.

---

## Usage

### Network API Endpoints

The MineLights server communicates with the client over the local network using a set of TCP and UDP endpoints.

---

#### 1. Handshake Endpoint

This is the primary endpoint for the client to connect, send its configuration, and receive a detailed layout of all available lighting hardware.

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
*   **`enabled_integrations`** (Array of Strings): A list of hardware SDKs the server should attempt to initialize.
*   **`disabled_devices`** (Array of Strings): A list of specific devices (formatted as `"Manufacturer|Model"`) that the server should ignore.

##### Server -> Client Response

After reconfiguring, the server sends back a JSON object, also prefixed with its length, detailing all discovered and mapped devices.

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

A simple endpoint for sending administrative commands to the server process.

*   **Protocol:** `TCP`
*   **Port:** `63213`
*   **Direction:** Client -> Server

##### Client -> Server Payload

A raw UTF-8 string representing the command.

**Available Commands:**
*   `"shutdown"`: Shuts down the MineLights server application.
*   `"restart"`: Restarts the server with standard user privileges.
*   `"restart_admin"`: Restarts the server with elevated (administrator) privileges.

---

#### 4. Discovery Endpoint

The server uses this endpoint to announce its presence on the local network, allowing the client to find it automatically without requiring a hardcoded IP address.

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

The server's behavior can be configured by editing the `config.json` file located in the same directory as `MineLights.exe`. If the file does not exist, it will be created with default values on the first run.

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
    "Msi"
  ],
  "DisabledDevices": [
    "Corsair|MM800 RGB POLARIS"
  ]
}
```

*   **`EnabledIntegrations`**: A list of hardware brands the server will try to initialize. You can remove entries from this list to prevent the server from hooking into certain SDKs, which can sometimes improve stability or resolve conflicts.

*   **`DisabledDevices`**: A list of specific devices to ignore. This is useful if a particular piece of hardware is causing issues but you still want to use other devices from the same brand. The format must be `"Manufacturer|Model"`, which you can find in the server's log files upon startup.

---
## Troubleshooting

If you encounter issues, follow these steps:

1.  **Check the Server Log:** The first and most important step is to check the `minelights-server.log` file (or the console output). It provides detailed information on which SDKs were initialized and which devices were mapped. Look for any `WARNING` or `ERROR` messages.

3.  **Hardware Not Detected by Server:**
    *   Ensure the corresponding manufacturer's software (iCUE, G HUB, Synapse) is running in the background.
    *   Try running `MineLights.exe` as an administrator, as some SDKs require it.
    *   Verify that your device is not listed in the `DisabledDevices` section of `config.json`.

4.  **No Connection Between Client and Server:**
    *   Make sure both are running on the same computer or at least the same local network.
    *   Check your firewall settings to ensure ports `63211-63214` are not being blocked.


## Roadmap

- **Windows Dynamic Lighting**: Integration with Windows Dynamic Lighting is planned for the future.
- **Improve Device Compatibility**: Continuously working to improve support for more devices across all SDKs.
- **Community Testing**: Ideally have testers, well test!

## Contributing

Contributions are welcome! Please fork the repository and submit a pull request.

## Contact

For any questions, suggestions, or bug reports, please [open an issue](https://github.com/megabytesme/MineLights-Server/issues) on the GitHub repository.
