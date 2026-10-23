package dev.lamarider

import dev.lamarider.errors.MetalamaDocs
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class MetalamaDocsTest {
    private val site = "https://doc.metalama.net/errors/lama0115"
    private val repo = "https://github.com/metalama/Metalama.Documentation/blob/release/2027.0/content/errors/lama0115.md"
    private val raw = "https://raw.githubusercontent.com/metalama/Metalama.Documentation/release/2027.0/content/errors/lama0115.md"
    private val search = "https://www.google.com/search?q=Metalama+LAMA0115"

    @Test
    fun `the article on the documentation site once it is live`() =
        assertEquals(site, MetalamaDocs.url("LAMA0115", siteLive = true) { it == site })

    @Test
    fun `the article in the documentation repository while the site has none`() =
        assertEquals(repo, MetalamaDocs.url("LAMA0115", siteLive = false) { it == raw })

    @Test
    fun `the repository article when the site is live but lacks that page`() =
        assertEquals(repo, MetalamaDocs.url("LAMA0115", siteLive = true) { it == raw })

    @Test
    fun `a web search when neither has it`() =
        assertEquals(search, MetalamaDocs.url("LAMA0115", siteLive = false) { false })

    @Test
    fun `the site counts as live when its first article answers`() {
        assertTrue(MetalamaDocs.siteIsLive { it == "https://doc.metalama.net/errors/lama0001" })
        assertFalse(MetalamaDocs.siteIsLive { false })
    }
}
