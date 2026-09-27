package uk.co.reliablesolutions.glassesremote.companion

import android.app.Activity
import android.content.Intent
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle

/**
 * Shows Android's screen-capture consent and hands the answer to [CompanionService]. Invisible
 * itself. Whole display only: the glasses' taps need screen coordinates, which a single-app
 * capture doesn't give.
 */
class ConsentActivity : Activity() {
    companion object {
        private const val REQUEST = 1
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (savedInstanceState != null) return
        val manager = getSystemService(MediaProjectionManager::class.java)
        val intent = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            manager.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
        } else {
            manager.createScreenCaptureIntent()
        }
        @Suppress("DEPRECATION")
        startActivityForResult(intent, REQUEST)
    }

    @Deprecated("Platform Activity result API; no AndroidX here")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQUEST) return
        startService(
            Intent(this, CompanionService::class.java)
                .setAction(CompanionService.ACTION_CONSENT)
                .putExtra(CompanionService.EXTRA_CODE, resultCode)
                .putExtra(CompanionService.EXTRA_DATA, data),
        )
        finish()
    }
}
