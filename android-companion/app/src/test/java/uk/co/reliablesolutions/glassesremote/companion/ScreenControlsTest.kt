package uk.co.reliablesolutions.glassesremote.companion

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ScreenControlsTest {
    private class Node(
        override val box: Box = Box(0, 0, 100, 100),
        override val visible: Boolean = true,
        override val clickable: Boolean = false,
        override val editable: Boolean = false,
        override val password: Boolean = false,
        override val checkable: Boolean = false,
        override val heading: Boolean = false,
        override val role: String = "",
        override val description: String = "",
        override val text: String = "",
        override val hint: String = "",
        override val viewId: String = "",
        val children: List<Node> = emptyList(),
    ) : UiNode {
        override val childCount get() = children.size
        override fun child(index: Int): UiNode? = children.getOrNull(index)
    }

    /** Claude's bottom row as the S25 showed it (2026-10-05): labels on the icons inside. */
    private val claude = Node(
        box = Box(0, 0, 1080, 2340),
        children = listOf(
            Node(box = Box(0, 115, 144, 259), clickable = true, children = listOf(Node(description = "Open menu, new feature available"))),
            Node(
                box = Box(87, 1864, 993, 2008), clickable = true, editable = true, text = "my secret draft",
                children = listOf(Node(text = "Type / for commands")),
            ),
            Node(box = Box(54, 1998, 198, 2142), clickable = true, children = listOf(Node(description = "Add context"), Node())),
            Node(box = Box(204, 1998, 533, 2142), clickable = true, children = listOf(Node(text = "Opus 5.5"), Node(text = "High"))),
            Node(box = Box(750, 1998, 882, 2142), clickable = true, children = listOf(Node(description = "Start speech input"))),
            Node(box = Box(882, 1998, 1026, 2142), clickable = true, description = "Send"),
            Node(box = Box(0, 300, 1080, 400), text = "a message, not pressable"),
        ),
    )

    @Test
    fun lists_what_you_can_press_with_labels_from_inside() {
        val controls = ScreenControls.onScreen(claude)
        assertEquals(
            listOf("Open menu, new feature available", "", "Add context", "Opus 5.5 High", "Start speech input", "Send"),
            controls.map { it.label },
        )
        assertEquals(listOf("button", "field", "button", "button", "button", "button"), controls.map { it.kind })
    }

    @Test
    fun a_field_gives_only_its_hint_never_its_text() {
        val field = Node(editable = true, clickable = true, text = "what I typed", hint = "Reply to Claude", children = listOf(Node(text = "what I typed")))
        assertEquals("Reply to Claude", ScreenControls.label(field))
        // A clickable card holding a field doesn't take its label from the field either.
        assertEquals("Card", ScreenControls.label(Node(clickable = true, children = listOf(field, Node(text = "Card")))))
    }

    @Test
    fun password_fields_are_left_out() {
        val root = Node(children = listOf(Node(editable = true, password = true, hint = "Password"), Node(editable = true, hint = "Email")))
        assertEquals(listOf("Email"), ScreenControls.onScreen(root).map { it.label })
        assertEquals(listOf("Email"), ScreenControls.walkable(root, WalkUnit.FIELD).map { it.label })
    }

    @Test
    fun hidden_and_flat_nodes_are_not_on_screen_but_can_be_walked_to() {
        val root = Node(
            children = listOf(
                Node(box = Box(0, 100, 100, 200), clickable = true, text = "shown"),
                Node(box = Box(0, 2025, 100, 2025), clickable = true, role = "link", text = "below"),
                Node(box = Box(0, 0, 100, 100), visible = false, clickable = true, text = "hidden"),
            ),
        )
        assertEquals(listOf("shown"), ScreenControls.onScreen(root).map { it.label })
        assertEquals(listOf("below"), ScreenControls.walkable(root, WalkUnit.LINK).map { it.label })
    }

    @Test
    fun a_match_wrapping_another_at_the_same_spot_counts_once() {
        val profile = Node(
            box = Box(48, 500, 348, 560), clickable = true, role = "link", description = "AeroSummit's profile",
            children = listOf(
                Node(box = Box(48, 500, 348, 560), clickable = true, role = "link", text = "avatar"),
                Node(box = Box(381, 500, 495, 560), clickable = true, role = "link", text = "2d ago"),
            ),
        )
        assertEquals(listOf("AeroSummit's profile", "2d ago"), ScreenControls.walkable(Node(children = listOf(profile)), WalkUnit.ITEM).map { it.label })
    }

    @Test
    fun units_pick_their_kind_of_thing() {
        val root = Node(
            children = listOf(
                Node(role = "article", children = listOf(Node(heading = true, text = "Post title"), Node(clickable = true, role = "link", text = "comments"))),
                Node(role = "navigation", text = "nav"),
                Node(clickable = true, text = "Upvote", checkable = true),
            ),
        )
        assertEquals(listOf("Post title"), ScreenControls.walkable(root, WalkUnit.HEADING).map { it.label })
        assertEquals(listOf("comments"), ScreenControls.walkable(root, WalkUnit.LINK).map { it.label })
        assertEquals(listOf("Post title comments"), ScreenControls.walkable(root, WalkUnit.ARTICLE).map { it.label })
        assertEquals(listOf("nav"), ScreenControls.walkable(root, WalkUnit.LANDMARK).map { it.label })
        assertEquals(listOf("link", "toggle"), ScreenControls.walkable(root, WalkUnit.ITEM).map { it.kind })
    }

    @Test
    fun a_walk_steps_on_from_the_last_or_starts_where_you_read() {
        val area = Box(0, 100, 1000, 2000)
        fun item(top: Int, bottom: Int = top + 50) = Control(Node(), Box(0, top, 100, bottom), "link", "", "")
        // Two above the screen (flat at its top), two on it, two below (flat at its bottom).
        val list = listOf(item(100, 100), item(100, 100), item(500), item(900), item(2000, 2000), item(2000, 2000))
        assertEquals(3, ScreenControls.step(list, from = 2, next = true, area = area))
        assertEquals(1, ScreenControls.step(list, from = 2, next = false, area = area))
        assertNull(ScreenControls.step(list, from = 5, next = true, area = area))
        assertNull(ScreenControls.step(list, from = 0, next = false, area = area))
        // Nothing walked yet (or scrolled away): the first or last on screen.
        assertEquals(2, ScreenControls.step(list, from = null, next = true, area = area))
        assertEquals(3, ScreenControls.step(list, from = null, next = false, area = area))
        // Nothing on screen: the first below, or the last above.
        val offScreen = listOf(item(100, 100), item(2000, 2000))
        assertEquals(1, ScreenControls.step(offScreen, from = null, next = true, area = area))
        assertEquals(0, ScreenControls.step(offScreen, from = null, next = false, area = area))
    }

    @Test
    fun ids_are_entry_names() {
        assertEquals("url_bar", ScreenControls.idName("com.android.chrome:id/url_bar"))
        assertEquals("", ScreenControls.idName(""))
    }
}
