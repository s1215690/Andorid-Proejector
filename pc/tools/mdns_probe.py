import socket, struct, time, subprocess, sys, json, os

ADB = r"C:\Users\s1215\AppData\Local\Android\Sdk\platform-tools\adb.exe"
IFACE = "192.168.0.215"
GRP = "224.0.0.251"


def enc_name(name):
    out = b""
    for part in name.split("."):
        if not part:
            continue
        out += bytes([len(part)]) + part.encode()
    return out + b"\x00"


def read_name(data, off):
    labels = []
    jumped = False
    start = off
    hops = 0
    while True:
        b = data[off]
        if b == 0:
            off += 1
            break
        if b >= 192:
            ptr = ((b - 192) * 256) + data[off + 1]
            if not jumped:
                start = off + 2
            jumped = True
            off = ptr
            hops += 1
            if hops > 8:
                break
            continue
        labels.append(data[off + 1:off + 1 + b].decode("latin1"))
        off += 1 + b
    return ".".join(labels), (start if jumped else off)


def parse(data):
    recs = []
    if len(data) < 12:
        return recs
    qd, an, ns, ar = struct.unpack(">HHHH", data[4:12])
    off = 12
    for _ in range(qd):
        _, off = read_name(data, off)
        off += 4
    for _ in range(an + ns + ar):
        if off >= len(data):
            break
        try:
            nm, off = read_name(data, off)
            t, c, ttl, rdlen = struct.unpack(">HHIH", data[off:off + 10])
            off += 10
            rd = data[off:off + rdlen]
            val = None
            if t == 12:
                val, _ = read_name(data, off)
            elif t == 33:
                pri, w, port = struct.unpack(">HHH", rd[:6])
                tgt, _ = read_name(data, off + 6)
                val = (tgt, port)
            elif t == 1:
                val = ".".join(str(x) for x in rd)
            recs.append((nm, t, val))
            off += rdlen
        except Exception:
            break
    return recs


def browse(types, secs=5.0):
    q = b"\x12\x34\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00"
    for n in types:
        q += enc_name(n) + b"\x00\x0c\x00\x01"
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("", 5353))
    s.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP,
                 struct.pack("4s4s", socket.inet_aton(GRP), socket.inet_aton(IFACE)))
    s.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(IFACE))
    s.sendto(q, (GRP, 5353))
    found = {}
    t = time.time()
    while time.time() - t < secs:
        s.settimeout(max(0.2, secs - (time.time() - t)))
        try:
            data, addr = s.recvfrom(9000)
        except Exception:
            break
        if addr[0] == IFACE:
            continue
        for nm, typ, val in parse(data):
            key = (addr[0], nm.rstrip(".").lower(), typ)
            found[key] = val
    s.close()
    return found


def ports_for(found, svc_type):
    """回傳 [(instance, ip, port)]"""
    out = []
    srv = {}
    addrs = {}
    insts = []
    for (ip0, k, typ), val in list(found.items()):
        if typ == 12 and isinstance(val, str) and svc_type in k:
            insts.append(val.rstrip(".").lower())
        if typ == 33 and isinstance(val, tuple):
            srv[k] = val
        if typ == 1 and isinstance(val, str):
            addrs[k] = val
    for inst in insts:
        s = srv.get(inst)
        if not s:
            continue
        tgt, port = s
        ip = addrs.get(tgt.rstrip(".").lower())
        if ip:
            out.append((inst.split(".")[0], ip, port))
    return out


def adb(*args):
    r = subprocess.run([ADB] + list(args), capture_output=True, text=True)
    return (r.stdout + r.stderr).strip()


if __name__ == "__main__":
    code = sys.argv[1] if len(sys.argv) > 1 else None
    sid = sys.argv[2] if len(sys.argv) > 2 else None
    print("adb devices 前:", adb("devices").replace("\n", " | "))
    found = browse(["_adb-tls-pairing._tcp.local", "_adb-tls-connect._tcp.local"], 6)
    pairs = ports_for(found, "_adb-tls-pairing")
    conns = ports_for(found, "_adb-tls-connect")
    print("配對服務:", pairs)
    print("連線服務:", conns)
    result = {"pairs": pairs, "conns": conns, "paired": False, "connected": None}
    if code:
        targets = [p for p in pairs if sid is None or p[0] == sid] or pairs
        for inst, ip, port in targets:
            out = adb("pair", "%s:%d" % (ip, port), code)
            print("adb pair %s:%d -> %s" % (ip, port, out))
            if "Successfully paired" in out:
                result["paired"] = True
                break
        time.sleep(2)
        found2 = browse(["_adb-tls-connect._tcp.local"], 6)
        conns2 = ports_for(found2, "_adb-tls-connect")
        print("配對後連線服務:", conns2)
        for inst, ip, port in conns2:
            out = adb("connect", "%s:%d" % (ip, port))
            print("adb connect %s:%d -> %s" % (ip, port, out))
            result["connected"] = "%s:%d" % (ip, port)
    print("adb devices 後:", adb("devices").replace("\n", " | "))
    with open("mdns_result.json", "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
