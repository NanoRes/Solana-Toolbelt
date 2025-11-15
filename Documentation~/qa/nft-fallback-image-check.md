# QA: NFT texture fallback

## Goal
Verify that `Solana.Unity.SDK.Nft.Nft.LoadTexture` skips gracefully or loads a fallback when NFT metadata omits `default_image`.

## Prerequisites
- Open the Wallet sample scene (or any runtime that calls `Nft.LoadTexture`).
- Have access to an NFT whose off-chain metadata contains an `image` field but no `default_image`, or be willing to edit the cached metadata JSON saved to `Application.persistentDataPath`.

## Steps
1. Load any NFT so the SDK caches its metadata to `<persistentDataPath>/<mint>.json`.
2. Edit the cached JSON and remove the `"default_image"` property while keeping at least one alternative image source (for example the top-level `image` field or a `properties.files[].uri`). Save the file and delete the cached `<mint>.png` so the texture is re-downloaded.
3. Re-run the texture load (re-open the scene or trigger the fetch). Confirm the texture renders correctly and no `UnityWebRequestException` is logged; the loader should silently rely on the alternate URL.
4. (Optional) Repeat the test with an NFT that has *no* usable image field to confirm the console reports `No image URL found for <mint>. Texture load skipped.` and the app continues without exceptions.

## Expected result
- NFTs with an alternate `image` source load successfully via the fallback URL.
- NFTs with no available image log a warning and skip texture hydration without crashing.
