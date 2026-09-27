# Proxy cores

KVN does not bundle the proxy cores. Drop the executable you want here:

    engines/sing-box/sing-box.exe     <- default engine
    engines/xray/xray.exe             <- alternative engine

Official releases:

* sing-box - https://github.com/SagerNet/sing-box/releases  (asset: sing-box-<version>-windows-amd64.zip)
* Xray-core - https://github.com/XTLS/Xray-core/releases     (asset: Xray-windows-64.zip)

Unpack the zip and copy the .exe into the matching folder above. KVN also accepts a
core that is already on your PATH, and the Settings page reports which it found.

Geo assets (geoip.dat / geosite.dat) are only needed by Xray and belong in
%APPDATA%\KVN\assets. sing-box downloads its rule-sets on demand instead.
