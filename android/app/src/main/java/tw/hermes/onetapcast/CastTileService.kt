package tw.hermes.onetapcast

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.widget.Toast
import java.util.concurrent.Executors

class CastTileService : TileService() {

    private val io = Executors.newSingleThreadExecutor()
    private val ui = Handler(Looper.getMainLooper())

    private val prefs get() = getSharedPreferences("onecast", Context.MODE_PRIVATE)

    override fun onStartListening() {
        val t = qsTile ?: return
        t.label = "一鍵投屏"
        t.state = if (prefs.getBoolean("casting", false)) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        t.updateTile()
    }

    override fun onClick() {
        super.onClick()
        val ctx = applicationContext
        Toast.makeText(ctx, "叫緊電腦投屏…", Toast.LENGTH_SHORT).show()
        io.execute {
            val casting = prefs.getBoolean("casting", false)
            val ip = prefs.getString("pc_ip", null)
            val port = prefs.getInt("pc_port", PcClient.HTTP_PORT)
            var target: PcClient.Pc? = null
            if (ip != null) {
                val ping = PcClient.http(ip, port, "/ping", 3000)
                if (ping.startsWith("ok")) target = PcClient.Pc(ip, port, "PC")
            }
            if (target == null) target = PcClient.discover()
            if (target == null) {
                ui.post { Toast.makeText(ctx, "搵唔到電腦", Toast.LENGTH_SHORT).show() }
                return@execute
            }
            prefs.edit().putString("pc_ip", target.ip).putInt("pc_port", target.port).apply()
            if (!casting) WirelessDebug.enableIfPossible(ctx)
            val path = if (casting) "/stop" else "/cast"
            val r = PcClient.http(target.ip, target.port, path)
            val nowCasting = if (casting) !r.startsWith("stopped") else r.startsWith("ok") || r.startsWith("already")
            prefs.edit().putBoolean("casting", nowCasting).apply()
            ui.post {
                val msg = when {
                    r.startsWith("stopped") -> "已停止投屏"
                    r.startsWith("already") -> "已經投屏緊"
                    r.startsWith("ok") -> "已開始投屏 ✓"
                    r.startsWith("no_device") -> "電腦連唔到手機 adb"
                    else -> "失敗：$r"
                }
                Toast.makeText(ctx, msg, Toast.LENGTH_SHORT).show()
                val t = qsTile
                if (t != null) {
                    t.state = if (nowCasting) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
                    t.updateTile()
                }
            }
        }
    }
}
