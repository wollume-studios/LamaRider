package dev.lamarider.errors

import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** At startup: whether Metalama's documentation site has the diagnostics' articles yet (see [MetalamaDocs]). */
class MetalamaDocsStartup : ProjectActivity {
    override suspend fun execute(project: Project) {
        withContext(Dispatchers.IO) { runCatching { MetalamaDocs.probe() } }
    }
}
