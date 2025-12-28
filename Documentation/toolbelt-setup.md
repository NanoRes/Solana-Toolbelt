# Toolbelt runtime setup (step-by-step)

This guide walks through creating the Toolbelt runtime setup in a Unity scene, wiring the `Solana_Configuration.asset`, and making a first wallet connect call.

## 1) Add `ToolbeltRuntime` to your scene

You can either:

- **Drag in the prefab:** `Runtime/Toolbelt/Web3.prefab`
  - This prefab already includes the runtime wiring with Web3 in one place.
- **Or add manually:** Create an empty GameObject and add the `ToolbeltRuntime` component.

> **Prefab reference:** `Runtime/Toolbelt/Web3.prefab`

## 2) Create and configure `Solana_Configuration.asset`

1. In Unity, create the configuration asset:
   - **Create ➜ Solana Toolbelt ➜ Solana Configuration**
2. Save it as **`Solana_Configuration.asset`** in:
   - `Runtime/Toolbelt/_Data`
3. Make sure the asset is discoverable by `ToolbeltRuntime`:
   - `ToolbeltRuntime` first tries `Resources.Load<SolanaConfiguration>("Solana_Configuration")`.
   - If it is not in a `Resources` folder, it will still try `Resources.FindObjectsOfTypeAll<SolanaConfiguration>()`.
   - In practice, keep the asset named **`Solana_Configuration`** and available in the project; the runtime will locate it.

## 3) Assign required dependencies on `ToolbeltRuntime`

On the `ToolbeltRuntime` component, assign the following fields:

- **Wallet Manager** → `WalletManager`
- **Solana NFT Access Manager** → `SolanaNftAccessManager`
- **JSON Uploader Behaviour** → component implementing `ILevelJsonUploader`
  - Example: `Runtime/Toolbelt/Uploaders/HTTP_JSON_Uploader/HttpJsonUploader`
- **NFT Storage Uploader Behaviour** → component implementing `INftStorageUploader`
  - Example: `Runtime/Toolbelt/Uploaders/Bundlr_Irys_Uploader/BundlrUploader` or `Runtime/Toolbelt/Services/NFT_Creation_Service/HttpNftStorageUploader`
- **UI Bridge Behaviour** → component implementing `IToolbeltUiBridge`
- **Storage Service Behaviour** → component implementing `ISolanaStorageService`
  - Example: `Runtime/Toolbelt/Services/Unity_Persistent_Storage_Service/UnityPersistentStorageService`

> Note: If no storage service is provided, the runtime will add `UnityPersistentStorageService` automatically.

## 4) Configure RPC endpoints and streaming URLs

In the `Solana_Configuration.asset` inspector:

- **RPC endpoints**
  - Set **`rpcUrls`** to the ordered list of HTTP RPC URLs.
  - Optional: Populate **`rpcEndpointPriorityList`** for explicit per-endpoint priority and retry settings.
  - **`currentRPCIndex`** controls which entry is preferred if `rpcEndpointPriorityList` is empty.
- **Streaming/WebSocket endpoints**
  - Set **`streamingRpcUrls`** to the ordered list of WebSocket endpoints.

At runtime, `ToolbeltRuntime` uses these to configure `Web3`:
- First RPC URL becomes `Web3.customRpc`.
- First streaming URL becomes `Web3.webSocketsRpc`.

## 5) First wallet connect flow

Once the runtime is in the scene and the configuration is assigned, you can call `WalletManager.ConnectAsync()` to prompt the user to connect.

Example MonoBehaviour:

```csharp
using UnityEngine;
using System.Threading.Tasks;

public class FirstWalletConnect : MonoBehaviour
{
    [SerializeField] private WalletManager walletManager;

    public async Task ConnectWalletAsync()
    {
        if (walletManager == null)
        {
            Debug.LogError("WalletManager reference is missing.");
            return;
        }

        bool connected = await walletManager.ConnectAsync();
        Debug.Log($"Wallet connected: {connected}");
    }
}
```

If you used the prefab, you can either:
- Reference the prefab’s `WalletManager` in your script, or
- Look it up via your scene hierarchy if you keep the runtime as a singleton GameObject.

---

### Quick checklist

- ✅ `ToolbeltRuntime` present in the scene (or `Runtime/Toolbelt/Web3.prefab` used).
- ✅ `Solana_Configuration.asset` exists at `Runtime/Toolbelt/_Data` and is discoverable.
- ✅ `WalletManager`, `SolanaNftAccessManager`, `ILevelJsonUploader`, `INftStorageUploader`, `IToolbeltUiBridge`, and `ISolanaStorageService` assigned.
- ✅ RPC URLs and streaming URLs configured in `Solana_Configuration.asset`.
- ✅ `WalletManager.ConnectAsync()` called from your UI flow.
