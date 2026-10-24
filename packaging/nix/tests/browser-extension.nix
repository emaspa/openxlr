# nix-instantiate --eval --strict --json --arg nixpkgs /path/to/nixpkgs this-file
{ nixpkgs ? <nixpkgs> }:
let
  lib = import (nixpkgs + "/lib");
  extension = "beeohanoffjpcofbjiiamkdammhcihaf";
  other = "cjpalhdlnbpafiamejdnhcphjbkeiagm";
  message = "services.openxlr.chromiumExtension requires programs.chromium.enable = true.";
  evaluate = { enabled ? true, companion ? false, policy ? false, extensions ? null }:
    (import (nixpkgs + "/nixos/lib/eval-config.nix") {
      system = "x86_64-linux";
      modules = [
        ../module.nix
        ({ pkgs, ... }: {
          system.stateVersion = "26.05";
          services.openxlr = {
            enable = enabled;
            package = pkgs.emptyDirectory;
            chromiumExtension = companion;
            lv2Plugins = [ ];
            clipGuard = false;
          };
          programs.chromium = { enable = policy; inherit extensions; };
        })
      ];
    }).config;
  rejected = config: map (a: a.message)
    (lib.filter (a: !a.assertion && a.message == message) config.assertions);
  off = evaluate { };
  selected = evaluate { companion = true; policy = true; extensions = [ other ]; };
  tests = {
    testDefaultLeavesPolicyAlone = { expr = off.programs.chromium.extensions; expected = null; };
    testDefaultNeedsNoBrowserPolicy = { expr = rejected off; expected = [ ]; };
    testDisabledServiceDoesNotInstall = {
      expr = (evaluate { enabled = false; companion = true; policy = true; }).programs.chromium.extensions;
      expected = null;
    };
    testOptInPreservesOtherExtensions = {
      expr = lib.sort builtins.lessThan selected.programs.chromium.extensions;
      expected = lib.sort builtins.lessThan [ extension other ];
    };
    testMissingPolicyIsRejected = {
      expr = rejected (evaluate { companion = true; });
      expected = [ message ];
    };
    testEnabledPolicyIsAccepted = { expr = rejected selected; expected = [ ]; };
  } // lib.genAttrs (map (name: "testPolicyFor" + name) [ "Chromium" "Chrome" "Brave" ]) (name: {
    expr = builtins.fromJSON selected.environment.etc.${
      if name == "testPolicyForChromium" then "chromium/policies/managed/default.json"
      else if name == "testPolicyForChrome" then "opt/chrome/policies/managed/default.json"
      else "brave/policies/managed/default.json"
    }.text;
    expected = { ExtensionInstallForcelist = selected.programs.chromium.extensions; };
  });
  failures = lib.runTests tests;
in
if failures == [ ] then builtins.attrNames tests
else throw (builtins.toJSON failures)
