"""等手機掃 QR 後自動配對 + 連線（繞過 adb 內建 mDNS 失效問題）。

用法: python qr_pair_watch.py <pairing_密碼> [期望嘅服務名] [秒數]
"""
import sys, time, json, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mdns_probe import browse, ports_for, adb

LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "qr_pair_watch.log")


def log(msg):
    line = time.strftime("%H:%M:%S ") + str(msg)
    print(line, flush=True)
    try:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


def main():
    code = sys.argv[1]
    want = sys.argv[2] if len(sys.argv) > 2 else None
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 240
    log("開始等手機掃碼… code=%s want=%s 上限=%ds" % (code, want, limit))
    deadline = time.time() + limit
    paired = False
    while time.time() < deadline and not paired:
        try:
            found = browse(["_adb-tls-pairing._tcp.local"], 2.5)
            pairs = ports_for(found, "_adb-tls-pairing")
        except Exception as e:
            log("mDNS 查詢出錯: %s" % e)
            pairs = []
        if pairs:
            log("見到配對服務: %s" % (pairs,))
            pairs.sort(key=lambda p: 0 if (want and p[0] == want) else 1)
            for inst, ip, port in pairs:
                out = adb("pair", "%s:%d" % (ip, port), code)
                log("adb pair %s:%d -> %s" % (ip, port, out))
                if "Successfully paired" in out:
                    paired = True
                    log("配對成功！實例=%s IP=%s" % (inst, ip))
                    break
    if not paired:
        log("逾時未配對成功（手機可能未掃碼或者 QR 已過期）")
        return 1

    time.sleep(2)
    for attempt in range(12):
        try:
            found = browse(["_adb-tls-connect._tcp.local"], 2.5)
            conns = ports_for(found, "_adb-tls-connect")
        except Exception as e:
            log("連線服務查詢出錯: %s" % e)
            conns = []
        if conns:
            log("見到連線服務: %s" % (conns,))
            for inst, ip, port in conns:
                out = adb("connect", "%s:%d" % (ip, port))
                log("adb connect %s:%d -> %s" % (ip, port, out))
                if "connected to" in out or "already connected" in out:
                    log("無線連線成功！ %s:%d" % (ip, port))
                    log("adb devices:\n" + adb("devices", "-l"))
                    with open(os.path.join(os.path.dirname(LOG), "known.txt"), "a", encoding="utf-8") as f:
                        f.write("%s:%d\n" % (ip, port))
                    return 0
        time.sleep(2)
    log("配對成功但連線失敗")
    return 1


if __name__ == "__main__":
    sys.exit(main())
