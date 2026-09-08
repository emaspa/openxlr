{ config, lib, pkgs, ... }:

let
  cfg = config.services.openxlr;
in
{
  options.services.openxlr = {
    enable = lib.mkEnableOption
      "OpenXLR, the control suite and PipeWire submixer for Elgato XLR interfaces";

    package = lib.mkOption {
      type = lib.types.package;
      description = "The OpenXLR package to use.";
    };

    clipGuard = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Make the SWH LADSPA plugins visible to the daemon so the software
        ClipGuard limiter works on devices that need it (XLR Dock).
      '';
    };

    yabridgePackage = lib.mkOption {
      type = lib.types.nullOr lib.types.package;
      default = null;
      description = ''
        Optional openxlr-yabridge companion package. Its matched bridge and
        controller are selected only for OpenXLR. Wine is its package dependency.
      '';
    };

    lv2Plugins = lib.mkOption {
      type = lib.types.listOf lib.types.package;
      default = [ pkgs.lsp-plugins ];
      defaultText = lib.literalExpression "[ pkgs.lsp-plugins ]";
      example = lib.literalExpression "[ pkgs.lsp-plugins pkgs.x42-plugins pkgs.calf ]";
      description = ''
        LV2 plugin packages offered as inserts on the XLR inputs and the
        mixes. They are put on the daemon's LV2_PATH together with
        ~/.lv2 and the system profile's lib/lv2.
      '';
    };
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ cfg.package ] ++ lib.optional (cfg.yabridgePackage != null) cfg.yabridgePackage;

    # Device access for regular users (uaccess tag).
    services.udev.packages = [ cfg.package ];

    # Keeps the XLR Dock's capture source always active; without it the
    # kernel starves capture when playback starts first and the mic
    # records silence.
    services.pipewire.wireplumber.configPackages = [ cfg.package ];

    # The submixer's send faders are PulseAudio-side streams inside
    # pipewire-pulse, which reaches systemd's default 1024 open files with a
    # few channels or mixes beyond the default layout and then drops nodes.
    systemd.user.services.pipewire-pulse.serviceConfig.LimitNOFILE = 65536;

    systemd.user.services.openxlr-daemon = {
      description = "OpenXLR audio daemon";
      after = [ "pipewire-pulse.service" "wireplumber.service" ];
      wantedBy = [ "default.target" ];
      unitConfig = {
        StartLimitIntervalSec = 300;
        StartLimitBurst = 3;
      };
      environment = {
        OPENXLR_BUILD_MIXER = "1";
        # Plugins load inside pw-cli, a child of the daemon, so this one
        # variable covers both the catalog scan and the filter chains.
        LV2_PATH = lib.concatStringsSep ":" (
          [ "%h/.lv2" "/run/current-system/sw/lib/lv2" ]
          ++ map (p: "${p}/lib/lv2") cfg.lv2Plugins);
      } // lib.optionalAttrs (cfg.yabridgePackage != null) {
        OPENXLR_YABRIDGE = "${cfg.yabridgePackage}/lib/openxlr/yabridge";
      } // lib.optionalAttrs cfg.clipGuard {
        LADSPA_PATH = "${pkgs.ladspaPlugins}/lib/ladspa";
      };
      serviceConfig = {
        Type = "notify";
        NotifyAccess = "main";
        WatchdogSec = 60;
        WatchdogSignal = "SIGTERM";
        TimeoutStartSec = 120;
        ExecStart = "${cfg.package}/bin/openxlr-daemon";
        TimeoutStopSec = 45;
        Restart = "on-failure";
        RestartSec = 3;
        NoNewPrivileges = true;
        PrivateTmp = true;
        ProtectSystem = "strict";
        ProtectControlGroups = true;
        ProtectKernelTunables = true;
        RestrictSUIDSGID = true;
        UMask = "0077";
        KeyringMode = "private";
        ProtectClock = true;
        ProtectHostname = true;
        ProtectKernelLogs = true;
        ProtectKernelModules = true;
        LockPersonality = true;
        RestrictNamespaces = true;
        CapabilityBoundingSet = "";
      };
    };
  };
}
