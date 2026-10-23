package dev.lamarider

import dev.lamarider.errors.MetalamaDocs
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class MetalamaDocsTest {
    @Test
    fun `the diagnostic's article when it is published`() =
        assertEquals("https://doc.metalama.net/errors/lama0115", MetalamaDocs.url("LAMA0115") { true })

    @Test
    fun `a web search while Metalama has no article for it`() =
        assertEquals("https://www.google.com/search?q=Metalama+LAMA0115", MetalamaDocs.url("LAMA0115") { false })
}
