package dev.lamarider.errors

import com.intellij.ide.BrowserUtil
import com.intellij.openapi.application.ApplicationManager
import java.net.HttpURLConnection
import java.net.URI
import java.net.URLEncoder

/**
 * The documentation of a Metalama diagnostic (LAMA0115): its article on the documentation site once Metalama publishes
 * them there (checked at startup), else the article in the documentation repository, else a web search.
 */
object MetalamaDocs {
    private const val SITE = "https://doc.metalama.net/errors/"
    private const val REPO_BLOB = "https://github.com/metalama/Metalama.Documentation/blob/release/2027.0/content/errors/"
    private const val REPO_RAW = "https://raw.githubusercontent.com/metalama/Metalama.Documentation/release/2027.0/content/errors/"

    /** Whether the site has the articles: probed at startup, then at most once a day. */
    @Volatile
    private var live: Boolean? = null

    @Volatile
    private var probedAt = 0L

    fun article(id: String) = SITE + id.lowercase()

    fun repository(id: String) = REPO_BLOB + id.lowercase() + ".md"

    fun search(id: String) = "https://www.google.com/search?q=" + URLEncoder.encode("Metalama $id", Charsets.UTF_8)

    /** [exists]: whether a URL answers 200 (following redirects). */
    fun siteIsLive(exists: (String) -> Boolean): Boolean = exists(SITE + "lama0001")

    /** Probes the site (startup activity); the result is used by [open] for a day. */
    fun probe() {
        live = siteIsLive(::exists)
        probedAt = System.currentTimeMillis()
    }

    fun url(id: String, siteLive: Boolean, exists: (String) -> Boolean): String = when {
        siteLive && exists(article(id)) -> article(id)
        exists(REPO_RAW + id.lowercase() + ".md") -> repository(id)
        else -> search(id)
    }

    fun open(id: String) = ApplicationManager.getApplication().executeOnPooledThread {
        if (live == null || System.currentTimeMillis() - probedAt > 24 * 60 * 60 * 1000L) probe()
        BrowserUtil.browse(url(id, live == true, ::exists))
    }

    private fun exists(url: String): Boolean = runCatching {
        var target = URI(url).toURL()
        repeat(5) { // redirects across hosts (doc.metalama.net -> doc.postsharp.net) aren't followed automatically
            val connection = (target.openConnection() as HttpURLConnection).apply {
                requestMethod = "HEAD"; connectTimeout = 3000; readTimeout = 3000; instanceFollowRedirects = false
            }
            val code = connection.responseCode
            if (code in 300..399) target = URI(url).resolve(connection.getHeaderField("Location")).toURL() else return@runCatching code == 200
        }
        false
    }.getOrDefault(false)
}
