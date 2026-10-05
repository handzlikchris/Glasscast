package uk.co.reliablesolutions.glassesremote.companion

import android.graphics.Rect
import android.view.accessibility.AccessibilityNodeInfo

/** A live accessibility node as [ScreenControls] reads it. */
class A11yNode(val info: AccessibilityNodeInfo) : UiNode {
    override val box: Box
        get() {
            val r = Rect()
            info.getBoundsInScreen(r)
            return Box(r.left, r.top, r.right, r.bottom)
        }
    override val visible get() = info.isVisibleToUser
    override val clickable get() = info.isClickable
    override val editable get() = info.isEditable
    override val password get() = info.isPassword
    override val checkable get() = info.isCheckable
    override val heading get() = info.isHeading
    override val role: String
        get() = runCatching { info.extras?.getCharSequence(CHROME_ROLE)?.toString() }.getOrNull().orEmpty()
    override val description get() = info.contentDescription?.toString().orEmpty()
    override val text get() = info.text?.toString().orEmpty()
    override val hint get() = info.hintText?.toString().orEmpty()
    override val viewId get() = info.viewIdResourceName.orEmpty()
    override val childCount get() = info.childCount
    override fun child(index: Int): UiNode? = info.getChild(index)?.let(::A11yNode)

    companion object {
        /** Where Chrome puts a web node's ARIA role ("link", "article", "navigation", …). */
        private const val CHROME_ROLE = "AccessibilityNodeInfo.chromeRole"
    }
}
