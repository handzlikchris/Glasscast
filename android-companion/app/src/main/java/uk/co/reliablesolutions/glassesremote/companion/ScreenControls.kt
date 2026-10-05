package uk.co.reliablesolutions.glassesremote.companion

/**
 * What the glasses' app profiles see of the followed app (architecture/app-profiles.md): the
 * things you can press, as boxes with a kind and a label, and the walk through them in reading
 * order. Knows no app. Pure: [UiNode] stands for an accessibility node so tests can fake a tree.
 *
 * Never reported: what's typed in a text field (a field's label is only its hint) and password
 * fields.
 */
interface UiNode {
    /** In screen pixels; flat (no height or width) for web content scrolled out of view. */
    val box: Box
    val visible: Boolean
    val clickable: Boolean
    val editable: Boolean
    val password: Boolean
    val checkable: Boolean
    val heading: Boolean
    /** Chrome's role for web content ("link", "article", "navigation", …), else "". */
    val role: String
    val description: String
    val text: String
    val hint: String
    /** "com.android.chrome:id/url_bar", or "" (Jetpack Compose apps have none). */
    val viewId: String
    val childCount: Int
    fun child(index: Int): UiNode?
}

data class Box(val left: Int, val top: Int, val right: Int, val bottom: Int) {
    val flat: Boolean get() = right <= left || bottom <= top

    fun near(other: Box, slack: Int = 4): Boolean =
        kotlin.math.abs(left - other.left) <= slack && kotlin.math.abs(top - other.top) <= slack &&
            kotlin.math.abs(right - other.right) <= slack && kotlin.math.abs(bottom - other.bottom) <= slack

    /** Inside [area] entirely. */
    fun within(area: Box): Boolean = !flat && left >= area.left && top >= area.top && right <= area.right && bottom <= area.bottom

    /** At least partly inside [area]. */
    fun touches(area: Box): Boolean = !flat && left < area.right && right > area.left && top < area.bottom && bottom > area.top
}

/** What a walk steps to (the glasses' `walk{unit}`). */
enum class WalkUnit(val wire: String) {
    /** Anything you can press: links, buttons, fields. */
    ITEM("item"),
    LINK("link"),
    HEADING("heading"),
    FIELD("field"),
    /** A post or story (`<article>`). */
    ARTICLE("article"),
    /** A part of the page: header, navigation, main, search, … */
    LANDMARK("landmark"),
    ;

    companion object {
        fun of(wire: String): WalkUnit? = entries.firstOrNull { it.wire == wire }
    }
}

/** One thing on screen for the glasses. [kind] and [label] go to the glasses as they are. */
data class Control(val node: UiNode, val box: Box, val kind: String, val label: String, val id: String)

object ScreenControls {
    const val MAX_CONTROLS = 64
    const val MAX_LABEL = 40
    const val MAX_ID = 40
    /** Web pages nest deeply (a Reddit comment sits ~30 levels down). */
    private const val MAX_DEPTH = 60
    /** Nodes visited per list (a Reddit post page had ~650). */
    private const val MAX_NODES = 3000

    private val LANDMARKS = setOf("banner", "navigation", "main", "complementary", "contentinfo", "region", "search", "form")

    /**
     * The things you can press, visible on screen, in reading order (tree order), at most
     * [MAX_CONTROLS]: for the glasses' `controls{}`.
     */
    fun onScreen(root: UiNode): List<Control> =
        collect(root, WalkUnit.ITEM, visibleOnly = true).take(MAX_CONTROLS)

    /** Everything of [unit] in the tree, on screen or not, in reading order: what a walk steps through. */
    fun walkable(root: UiNode, unit: WalkUnit): List<Control> = collect(root, unit, visibleOnly = false)

    /**
     * The index a walk lands on in [list]. From [from] (the last one walked to, if it's still on
     * screen) one step on. Otherwise, so a walk after a scroll starts where you're reading: the
     * first one on screen ([area]) going [next], the last going back; with none on screen, the
     * first below it or the last above it (Chrome squashes what's scrolled out of view flat onto
     * the top or bottom edge). Null at the end.
     */
    fun step(list: List<Control>, from: Int?, next: Boolean, area: Box): Int? {
        if (from != null) {
            val i = if (next) from + 1 else from - 1
            return i.takeIf { it in list.indices }
        }
        val shown = list.indices.filter { list[it].box.touches(area) }
        val middle = (area.top + area.bottom) / 2
        return if (next) {
            shown.firstOrNull() ?: list.indices.firstOrNull { list[it].box.top >= middle }
        } else {
            shown.lastOrNull() ?: list.indices.lastOrNull { list[it].box.bottom <= middle }
        }
    }

    fun matches(node: UiNode, unit: WalkUnit): Boolean {
        if (node.password) return false
        return when (unit) {
            WalkUnit.ITEM -> node.clickable || node.editable
            WalkUnit.LINK -> node.role == "link"
            WalkUnit.HEADING -> node.heading || node.role == "heading"
            WalkUnit.FIELD -> node.editable
            WalkUnit.ARTICLE -> node.role == "article"
            WalkUnit.LANDMARK -> node.role in LANDMARKS
        }
    }

    fun kind(node: UiNode): String = when {
        node.editable -> "field"
        node.heading || node.role == "heading" -> "heading"
        node.role == "link" -> "link"
        node.checkable -> "toggle"
        node.clickable -> "button"
        else -> "text"
    }

    /**
     * A field: its hint only (what's typed in it never leaves the phone). Anything else: its
     * description, else its text, else those of the nodes inside it (Compose puts a button's
     * label on the icon inside it), skipping fields.
     */
    fun label(node: UiNode): String {
        if (node.editable) return node.hint.trim().take(MAX_LABEL)
        val own = node.description.ifBlank { node.text }.trim()
        if (own.isNotEmpty()) return own.take(MAX_LABEL)
        val parts = StringBuilder()
        inner(node, parts, 0, intArrayOf(200))
        return parts.toString().trim().take(MAX_LABEL)
    }

    private fun inner(node: UiNode, into: StringBuilder, depth: Int, budget: IntArray) {
        for (i in 0 until node.childCount) {
            if (into.length >= MAX_LABEL || depth > 12 || --budget[0] < 0) return
            val child = node.child(i) ?: continue
            if (child.editable || child.password) continue
            val own = child.description.ifBlank { child.text }.trim()
            if (own.isNotEmpty()) {
                if (into.isNotEmpty()) into.append(' ')
                into.append(own)
            } else {
                inner(child, into, depth + 1, budget)
            }
        }
    }

    /** The entry name of a resource id: "url_bar" from "com.android.chrome:id/url_bar". */
    fun idName(viewId: String): String = viewId.substringAfter(":id/", "").take(MAX_ID)

    private fun collect(root: UiNode, unit: WalkUnit, visibleOnly: Boolean): List<Control> {
        val found = ArrayList<Control>()
        val budget = intArrayOf(MAX_NODES)
        visit(root, unit, visibleOnly, 0, budget, found, ancestors = emptyList())
        return found
    }

    /**
     * Pre-order, so the list is in reading order. A match that only wraps an ancestor match at
     * the same spot (Reddit's avatar inside the profile link) is left out; one with a box of its
     * own (a link inside a card) is kept.
     */
    private fun visit(
        node: UiNode,
        unit: WalkUnit,
        visibleOnly: Boolean,
        depth: Int,
        budget: IntArray,
        found: MutableList<Control>,
        ancestors: List<Box>,
    ) {
        if (depth > MAX_DEPTH || --budget[0] < 0) return
        if (node.password) return
        if (visibleOnly && !node.visible) return
        var mine = ancestors
        if (matches(node, unit)) {
            val box = node.box
            val duplicate = ancestors.any { it.near(box) }
            val shown = !visibleOnly || !box.flat
            if (!duplicate && shown) {
                found += Control(node, box, kind(node), label(node), idName(node.viewId))
                mine = ancestors + box
            }
        }
        for (i in 0 until node.childCount) {
            val child = node.child(i) ?: continue
            visit(child, unit, visibleOnly, depth + 1, budget, found, mine)
        }
    }
}
