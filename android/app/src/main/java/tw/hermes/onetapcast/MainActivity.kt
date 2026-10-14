package tw.hermes.onetapcast

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import com.google.android.material.button.MaterialButton
import java.util.concurrent.Executors

class MainActivity : AppCompatActivity() {

    private lateinit var tvStatus: TextView
    private lateinit var tvPc: TextView
    private val io = Executors.newSingleThreadExecutor()
    private val ui = Handler(Looper.getMainLooper())

    @Volatile
    private var pc: PcClient.Pc? = null

    private val prefs by lazy { getSharedPreferences("onecast", Context.MODE_PRIVATE) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        tvStatus = findViewById(R.id.tvStatus)
        tvPc = findViewById(R.id.tvPc)

        val lastIp = prefs.getString("pc_ip", null)
        if (lastIp != null) {
            pc = PcClient.Pc(lastIp, prefs.getInt("pc_port", PcClient.HTTP_PORT),
                    prefs.getString("pc_name", "PC") ?: "PC")
        }
        refreshPcLabel()

        findViewById<MaterialButton>(R.id.btnCast).setOnClickListener { doCast() }
        findViewById<MaterialButton>(R.id.btnFind).setOnClickListener { findPc() }
        findViewById<MaterialButton>(R.id.btnStop).setOnClickListener { doStop() }
        findViewById<MaterialButton>(R.id.btnCast).setOnLongClickListener {
            doStop()
            true
        }

        // 可以唔用 UI 直接觸發（adb / 自動化工具 / 捷徑）：
        //   adb shell am start -n tw.hermes.onetapcast/.MainActivity --es do cast
        if (!handleAction(intent)) findPc()
    }

    override fun onResume() {
        super.onResume()
        if (pc == null) findPc()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        // App 已經開住時再收到 --es do cast/stop 都要做嘢
        handleAction(intent)
    }

    private fun handleAction(it: Intent?): Boolean {
        val act = it?.getStringExtra("do") ?: return false
        if (act.equals("cast", true)) {
            doCast()
            return true
        }
        if (act.equals("stop", true)) {
            doStop()
            return true
        }
        return false
    }

    private fun refreshPcLabel() {
        val p = pc
        tvPc.text = if (p == null) "電腦：未搵到" else "電腦：${p.name}（${p.ip}）"
    }

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    private fun savePc(p: PcClient.Pc) {
        prefs.edit().putString("pc_ip", p.ip).putInt("pc_port", p.port).putString("pc_name", p.name).apply()
    }

    private fun findPc() {
        tvStatus.text = "搵緊電腦…（要同一個 Wi-Fi）"
        io.execute {
            val found = PcClient.discover()
            ui.post {
                if (found != null) {
                    pc = found
                    savePc(found)
                    refreshPcLabel()
                    tvStatus.text = "已搵到電腦 ✓ 按上面大掣即投屏"
                } else {
                    refreshPcLabel()
                    tvStatus.text = "搵唔到電腦 — 檢查電腦 App 有冇開、同一個 Wi-Fi"
                }
            }
        }
    }

    private fun ensurePc(): PcClient.Pc? {
        pc?.let { p ->
            val ping = PcClient.http(p.ip, p.port, "/ping", 3000)
            if (ping.startsWith("ok")) return p
        }
        val found = PcClient.discover() ?: return null
        pc = found
        savePc(found)
        return found
    }

    private fun doCast() {
        tvStatus.text = "叫電腦開投屏…"
        io.execute {
            val p = ensurePc()
            if (p == null) {
                ui.post {
                    refreshPcLabel()
                    tvStatus.text = "搵唔到電腦 — 按「重新搵電腦」再試"
                }
                return@execute
            }
            val wd = WirelessDebug.enableIfPossible(this)
            val r = PcClient.http(p.ip, p.port, "/cast")
            ui.post {
                refreshPcLabel()
                showResult(r, wd)
            }
        }
    }

    private fun doStop() {
        io.execute {
            val p = ensurePc()
            if (p != null) PcClient.http(p.ip, p.port, "/stop", 8000)
            ui.post { tvStatus.text = "已叫電腦停止投屏" }
        }
    }

    private fun showResult(r: String, wd: Boolean) {
        when {
            r.startsWith("already") -> {
                tvStatus.text = "電腦已經投屏緊"
                toast("已經投屏緊")
            }
            r.startsWith("ok") -> {
                tvStatus.text = "已開始投屏 ✓ 睇電腦螢幕"
                toast("已開始投屏 ✓")
            }
            r.startsWith("no_device") -> {
                tvStatus.text = if (wd)
                    "電腦連唔到手機 adb — 喺電腦 App 掃 QR 或用 USB 一次"
                else
                    "手機「無線偵錯」熄咗 — 開返"
                toast("電腦連唔到手機")
            }
            r.startsWith("scrcpy_fail") -> {
                tvStatus.text = "電腦 scrcpy 啟動失敗"
                toast("scrcpy 啟動失敗")
            }
            else -> {
                tvStatus.text = "出錯：$r"
                toast("出錯")
            }
        }
    }
}
