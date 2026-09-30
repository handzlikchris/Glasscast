package uk.co.reliablesolutions.glassesremote.companion

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class InputProtocolTest {
    @Test
    fun parses_every_glasses_message() {
        assertEquals(InputCommand.Tap(0.25, 0.5), InputProtocol.parse("""{"type":"tap","x":0.25,"y":0.5}"""))
        assertEquals(InputCommand.LongPress(1.0, 0.0), InputProtocol.parse("""{"type":"longPress","x":1,"y":0}"""))
        assertEquals(
            InputCommand.Swipe(0.5, 0.7, 0.5, 0.3, 220),
            InputProtocol.parse("""{"type":"swipe","x1":0.5,"y1":0.7,"x2":0.5,"y2":0.3,"ms":220}"""),
        )
        assertEquals(InputCommand.Nav(NavAction.RECENTS), InputProtocol.parse("""{"type":"nav","action":"recents"}"""))
        assertEquals(InputCommand.TypeText("hello"), InputProtocol.parse("""{"type":"typeText","text":"hello"}"""))
        assertEquals(InputCommand.Key(KeyName.ENTER), InputProtocol.parse("""{"type":"key","key":"Enter"}"""))
        assertEquals(InputCommand.Key(KeyName.SEND), InputProtocol.parse("""{"type":"key","key":"Send"}"""))
        assertEquals(InputCommand.Ping(3.0), InputProtocol.parse("""{"type":"ping","t":3}"""))
        assertEquals(InputCommand.End, InputProtocol.parse("""{"type":"end"}"""))
        assertEquals(null, InputProtocol.parse("""{"type":"end","now":true}"""))
        assertEquals(InputCommand.DoubleTap(0.5, 0.5), InputProtocol.parse("""{"type":"doubleTap","x":0.5,"y":0.5}"""))
        assertEquals(InputCommand.SwitchApp(previous = true), InputProtocol.parse("""{"type":"switchApp","dir":"previous"}"""))
        assertEquals(InputCommand.SwitchApp(previous = false), InputProtocol.parse("""{"type":"switchApp","dir":"next"}"""))
        assertEquals(
            InputCommand.Touch(TouchPhase.MOVE, 0.25, 1.0),
            InputProtocol.parse("""{"type":"touch","phase":"move","x":0.25,"y":3}"""),
        )
    }

    @Test
    fun clamps_positions_and_durations() {
        assertEquals(InputCommand.Tap(1.0, 0.0), InputProtocol.parse("""{"type":"tap","x":7,"y":-2}"""))
        assertEquals(
            InputCommand.Swipe(0.0, 0.0, 1.0, 1.0, InputProtocol.SWIPE_MAX_MS),
            InputProtocol.parse("""{"type":"swipe","x1":0,"y1":0,"x2":1,"y2":1,"ms":99999}"""),
        )
    }

    @Test
    fun text_never_carries_a_line_break() {
        assertEquals(InputCommand.TypeText("one two"), InputProtocol.parse("""{"type":"typeText","text":"one\ntwo"}"""))
        assertNull(InputProtocol.parse("""{"type":"typeText","text":" \n "}"""))
        assertNull(InputProtocol.parse("""{"type":"typeText","text":"${"a".repeat(501)}"}"""))
    }

    @Test
    fun rejects_anything_else() {
        listOf(
            """not json""",
            """{"type":"tap","x":0.5}""",
            """{"type":"tap","x":0.5,"y":0.5,"extra":1}""",
            """{"type":"tap","x":"0.5","y":0.5}""",
            """{"type":"nav","action":"power"}""",
            """{"type":"key","key":"Ctrl+Alt+Del"}""",
            """{"type":"launch","package":"com.example"}""",
            """{"type":"swipe","x1":0,"y1":0,"x2":1,"y2":1}""",
            """{"x":1}""",
            """{"type":"fitWindow"}""",
            """{"type":"setRegion","x":0,"y":0,"width":1,"height":1}""",
            """{"type":"touch","phase":"hover","x":0,"y":0}""",
            """{"type":"switchApp","dir":"sideways"}""",
            """{"type":"switchApp","package":"com.example"}""",
            """{"type":"touch","phase":"down","x":0}""",
        ).forEach { assertNull(it, InputProtocol.parse(it)) }
    }
}
