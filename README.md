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

### V2 endpoint

V2 uses a single bidirectional **TLS TCP connection** for authentication, device discovery, requests, and lighting frames. It is a framed protocol, not an HTTP API: the JSON examples below are message payloads, not standalone TCP messages. The local port is chosen by the OS; there is no fixed V2 UDP port.

#### 1. Discover and pair

For a client running under the same Windows account as the server, read `%LOCALAPPDATA%\MineLights\v2\endpoint.json`:

```json
{
  "schema_version": 1,
  "server_id": "d2c4f8c0-973f-42c5-bcd8-74d7d34d33a2",
  "server_name": "My-PC",
  "host": "127.0.0.1",
  "port": 49152,
  "tls_certificate_sha256": "<64-character SHA-256 fingerprint>",
  "protocol_major": 2,
  "protocol_minor": 0
}
```

Connect to its `host` and `port` with TLS 1.2 or newer and verify the SHA-256 fingerprint of the server's DER-encoded certificate against `tls_certificate_sha256`. For first-time local enrollment, read the separate 32-byte `local-enrollment.key` in the same protected directory. The client generates and securely stores its own UUID and 32-byte secret, authenticates using the enrollment key, registers its secret, and reconnects using that secret. Local enrollment is accepted only from loopback.

For a remote client, enable **Server Connections → Remote access** in the tray, select the interfaces and port, then use **Export one-time pairing file**. Transfer the `.mlpair` file securely to the client. It contains the remote endpoint, certificate fingerprint, and a one-time pairing code that expires after five minutes. The client uses that code to enroll, then reconnects using its own stored secret. Remote access is off by default; configure any VPN or network routing separately.

Keep the local enrollment key, pairing file, and client secrets private. The endpoint document is discovery data, not a credential. The tray can revoke individual paired clients.

#### 2. Frame messages and authenticate

Every message has a 16-byte, big-endian header followed by a UTF-8 JSON payload:

| Offset | Bytes | Field |
| --- | ---: | --- |
| 0 | 4 | ASCII magic `MLV2` |
| 4 | 2 | Protocol major (`2`) |
| 6 | 2 | Protocol minor (`0`) |
| 8 | 2 | Message type |
| 10 | 2 | Flags (`0`) |
| 12 | 4 | Payload length in bytes |

Send `CLIENT_HELLO` (`0x0001`) first. For example, a lighting client requesting two providers sends:

```json
{
  "client_id": "2ee8316a-cda0-4f2a-9f3d-25768269160e",
  "app_id": "example-client",
  "app_version": "1.0.0",
  "min_minor": 0,
  "max_minor": 0,
  "capabilities": [],
  "requested_integrations": ["Corsair", "Logitech"]
}
```

The server replies with `SERVER_HELLO` (`0x0002`), including its ID, selected minor version, and a random nonce. Send `CLIENT_AUTH` (`0x0003`):

```json
{
  "credential_type": "local_enrollment",
  "credential_id": "local",
  "client_nonce": "<32 random bytes encoded as unpadded base64url>",
  "proof": "<HMAC-SHA-256 proof encoded as unpadded base64url>"
}
```

The proof is `HMAC-SHA-256(key, message)`, where `key` is the local enrollment key, one-time pairing code, or registered client secret. Construct `message` by concatenating these bytes in order:

```text
ASCII("MineLights Protocol V2") || 0x00 || credential_type_ascii || 0x00 ||
credential_id_ascii || 0x00 || server_id_ascii || 0x00 || client_id_ascii ||
server_nonce[32] || client_nonce[32] || major_u16_be || minor_u16_be
```

There is no separator after `client_id_ascii`. Decode the server nonce from the hello before building the proof. For a registered client, use `client_secret` and its client UUID as the credential type and ID; remote enrollment uses `pairing_code` and the pairing ID from the file. The server replies with `AUTH_RESULT` (`0x0004`). Provider interest is applied only after normal client authentication succeeds; a control-only or enrollment connection should request an empty integration list.

After a successful enrollment `AUTH_RESULT`, send a `REQUEST` (`0x0040`) to register the client:

```json
{
  "request_id": "25b12412-437c-45f5-b41f-634e0149b56f",
  "operation": "client.register",
  "params": {
    "client_id": "2ee8316a-cda0-4f2a-9f3d-25768269160e",
    "client_secret": "<32 random bytes encoded as unpadded base64url>",
    "app_id": "example-client",
    "app_version": "1.0.0",
    "display_name": "Example client",
    "requested_scopes": ["devices.read", "lighting.write"]
  }
}
```

The registration sends the newly generated client secret inside the encrypted TLS connection. Check the `RESPONSE` (`0x0041`), close the enrollment connection, and reconnect using `client_secret` authentication. The [full protocol specification](https://github.com/megabytesme/MineLights/blob/5a558d8743fc0fa1fe488dde4fa2fda091c2fa5c/docs/PROTOCOL_V2.md#tls-identity-and-pairing) describes the complete handshake and error handling.

#### 3. Discover devices and acquire a lease

After normal authentication, the server sends a `DEVICE_LIST` (`0x0010`) snapshot. It can later send `DEVICE_CHANGED` (`0x0011`) as devices or providers change. A client can also send a `REQUEST` (`0x0040`) for a fresh snapshot:

```json
{
  "request_id": "42f0497d-9e17-4af5-98d8-8e0435b61e1c",
  "operation": "devices.get",
  "params": {}
}
```

The corresponding `RESPONSE` (`0x0041`) contains the same `request_id`, an `ok` flag, and a `result` containing `revision` and `devices`. A shortened example:

```json
{
  "request_id": "42f0497d-9e17-4af5-98d8-8e0435b61e1c",
  "ok": true,
  "result": {
    "revision": 1,
    "devices": [
      {
        "device_id": "<stable device ID>",
        "name": "Keyboard",
        "vendor": "Corsair",
        "integration": "Corsair",
        "provider": "Corsair",
        "legacy_device_key": "Corsair|Keyboard",
        "elements": [{ "element_id": 0, "key": "LCTRL" }],
        "capabilities": ["rgb", "keyboard-key-map"]
      }
    ]
  }
}
```

Each device has a stable `device_id`, name, vendor, integration, elements, and capabilities; elements include an `element_id` and may include a key name. Optional `provider` and `legacy_device_key` fields identify the RGB.NET provider and the old `manufacturer|model` key.

Before sending colors, request a lease for the desired device IDs:

```json
{
  "request_id": "061a9a62-cf15-40ad-9ab7-709d6951bafb",
  "operation": "lighting.acquire",
  "params": { "device_ids": ["<device_id from DEVICE_LIST>"] }
}
```

A successful response includes `granted_device_ids` and `busy_device_ids`; a partial grant is possible. If none can be acquired, the server returns `LEASE_BUSY`. The server tray lets its owner assign paired clients High, Normal, or Low priority. A higher-priority client may preempt a lower-priority lease; a client receiving `LEASE_REQUIRED` should reacquire before sending more frames.

#### 4. Send lighting frames

Send a `LIGHTING_FRAME` (`0x0020`) over the same authenticated connection. Use IDs from the device snapshot and a lease held by this session:

```json
{
  "sequence": 1,
  "ttl_ms": 500,
  "targets": [
    {
      "device_id": "<device_id from DEVICE_LIST>",
      "colors": [
        { "element_id": 0, "r": 255, "g": 0, "b": 0 },
        { "element_id": 1, "r": 0, "g": 255, "b": 0 }
      ]
    }
  ]
}
```

Increment `sequence` for each frame and set `ttl_ms` from 1 to 1000. Valid frames renew the session's five-second lease; `PING` alone does not. The server restores idle lighting when a frame expires or its lease ends. Send a `lighting.release` request with `device_ids` when finished; disconnecting also releases the session's leases.

Use `integrations.set` with a `providers` array to update this session's requested providers. The server combines requests from clients while respecting the owner's server-wide settings; one client cannot disable a provider needed by another. An authenticated client can also call `server.info` or request `server.shutdown`. Shutdown is refused while another authenticated session is connected or legacy access is enabled. Restart and configuration remain tray controls.

For all message types, authentication details, scope requirements, error responses, and compatibility behavior, see the [complete V2 protocol specification](https://github.com/megabytesme/MineLights/blob/5a558d8743fc0fa1fe488dde4fa2fda091c2fa5c/docs/PROTOCOL_V2.md).

<details>
<summary>Deprecated: legacy V1 endpoints</summary>

### Legacy V1 endpoints (only when explicitly enabled)

Older clients use fixed loopback endpoints without authentication. This compatibility mode is disabled by default and can be enabled from the tray menu. It never enables the old raw command endpoint. Each legacy handshake filters its device list by that client's enabled integrations and disabled device IDs without changing the server owner's settings or other clients' devices. The server owner must also enable a provider in `config.json` for its devices to be available.

---

#### 1. Handshake Endpoint

This endpoint lets a legacy client request a device layout filtered by its own settings. The server owner's integration and device restrictions still apply.

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
*   **`enabled_integrations`** and **`disabled_devices`**: Filter the layout returned to this client. They do not change the server owner's configuration or other clients' settings.

##### Server -> Client Response

The server sends the filtered device layout as a JSON object prefixed with its length.

**Structure:**
```json
{
  "devices": [
    {
      "sdk": "Corsair",
      "name": "K100 RGB",
      "leds": [0, 1, 2, 3],
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
      "leds": [174, 175, 176],
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

Older Minecraft clients listen on this port for this message and can then initiate the **Handshake** process. Current MineLights clients use V2 instead.

</details>

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
    *   For a local V2 connection, run the client and server under the same Windows account and check `%LOCALAPPDATA%\MineLights\v2\endpoint.json` and the server log.
    *   For a remote V2 connection, enable the intended interface in the tray, transfer a fresh pairing file securely, and check the network path and firewall.
    *   Older V1 clients require explicit legacy access. If devices do not appear after approval, refresh the client or restart it.


## Roadmap

- **Windows Dynamic Lighting**: Integration with Windows Dynamic Lighting is planned for the future.
- **Improve Device Compatibility**: Continuously working to improve support for more devices across all SDKs.
- **Community Testing**: Ideally have testers, well test!

## Contributing

Contributions are welcome! Please fork the repository and submit a pull request.

## Contact

For any questions, suggestions, or bug reports, please [open an issue](https://github.com/megabytesme/MineLights-Server/issues) on the GitHub repository.
