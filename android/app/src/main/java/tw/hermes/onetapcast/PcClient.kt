package tw.hermes.onetapcast

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.net.URL

object PcClient {
    const val UDP_PORT = 8766
    const val HTTP_PORT = 8765

    data class Pc(val ip: String, val port: Int, val name: String)

    private fun broadcastAddrs(): List<InetAddress> {
        val list = mutableListOf<InetAddress>()
        try { list.add(InetAddress.getByName("255.255.255.255")) } catch (e: Exception) { }
        try {
            for (nif in NetworkInterface.getNetworkInterfaces()) {
                if (!nif.isUp || nif.isLoopback) continue
                for (ia in nif.interfaceAddresses) {
                    val b = ia.broadcast
                    if (b != null && b.address.size == 4) list.add(b)
                }
            }
        } catch (e: Exception) { }
        return list
    }

    fun discover(timeoutMs: Int = 2600): Pc? {
        val s = DatagramSocket()
        try {
            try { s.broadcast = true } catch (e: Exception) { }
            s.soTimeout = 350
            val msg = "ANDROIDCAST-DISCOVER".toByteArray()
            for (a in broadcastAddrs()) {
                try { s.send(DatagramPacket(msg, msg.size, a, UDP_PORT)) } catch (e: Exception) { }
            }
            val end = System.currentTimeMillis() + timeoutMs
            val buf = ByteArray(512)
            while (System.currentTimeMillis() < end) {
                val p = DatagramPacket(buf, buf.size)
                try {
                    s.receive(p)
                } catch (e: SocketTimeoutException) {
                    continue
                } catch (e: Exception) {
                    break
                }
                val txt = String(p.data, 0, p.length).trim()
                if (txt.startsWith("ANDROIDCAST-PC")) {
                    val parts = txt.split(" ")
                    val port = parts.getOrNull(1)?.toIntOrNull() ?: HTTP_PORT
                    val name = parts.getOrNull(2) ?: "PC"
                    return Pc(p.address.hostAddress ?: "", port, name)
                }
            }
        } finally {
            try { s.close() } catch (e: Exception) { }
        }
        return null
    }

    fun http(ip: String, port: Int, path: String, timeoutMs: Int = 25000): String {
        return try {
            val c = URL("http://$ip:$port$path").openConnection() as HttpURLConnection
            c.connectTimeout = 4000
            c.readTimeout = timeoutMs
            try {
                c.inputStream.bufferedReader().readText().trim()
            } finally {
                c.disconnect()
            }
        } catch (e: Exception) {
            "error " + (e.message ?: e.javaClass.simpleName)
        }
    }
}
