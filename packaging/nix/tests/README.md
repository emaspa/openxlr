# NixOS module checks

Evaluate the browser opt-in against an available nixpkgs checkout, without
building OpenXLR or fetching a Store extension:

```sh
nix-instantiate --eval --strict --json --arg nixpkgs /path/to/nixpkgs \
  packaging/nix/tests/browser-extension.nix
```

The checkout pinned in `flake.lock` is suitable. The command uses its real
NixOS and Chromium modules. It exits unsuccessfully on a failed check.
No browser is launched and no system configuration is activated. A live
Store listing and a browser restart are still needed to verify the actual
installation prompt.
