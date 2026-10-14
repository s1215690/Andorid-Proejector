package tw.hermes.onetapcast

import android.content.Context
import android.provider.Settings

object WirelessDebug {
    fun isEnabled(ctx: Context): Boolean {
        return try {
            Settings.Global.getInt(ctx.contentResolver, "adb_wifi_enabled", 0) == 1
        } catch (e: Exception) {
            false
        }
    }

    fun enableIfPossible(ctx: Context): Boolean {
        if (isEnabled(ctx)) return true
        return try {
            Settings.Global.putInt(ctx.contentResolver, "adb_wifi_enabled", 1)
            Thread.sleep(1800)
            isEnabled(ctx)
        } catch (e: Exception) {
            false
        }
    }
}
