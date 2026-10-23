package dev.lamarider.uitest

import com.intellij.driver.sdk.invokeAction
import com.intellij.driver.sdk.waitFor
import org.junit.jupiter.api.Test
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * Every quick-fix for Metalama diagnostics, on Metalama's own error samples (samples/MetalamaFixSamples): each fix is
 * applied and the code compared with the sample's fixed version. The samples are opened in batches (Batch01.sln ...):
 * Rider watches every project folder and Linux allows 128 inotify instances per user. Reports: build/fix-selftest-NN.json.
 */
class FixesUiTest : UiTestBase() {
    @Test
    fun `quick-fixes turn Metalama's error samples into their fixed versions`() {
        val batches = Path.of(System.getProperty("lamarider.sample")).resolveSibling("MetalamaFixSamples")
            .toFile().list { _, name -> name.startsWith("Batch") && name.endsWith(".sln") }!!.map { it.removeSuffix(".sln") }.sorted()
        val reports = Path.of(System.getProperty("lamarider.plugin")).parent.parent
        for (batch in batches) {
            withRider("fixes-$batch", sample = "MetalamaFixSamples", solution = batch) {
                val report = solutionDir.resolve("fix-selftest.json")
                val progress = solutionDir.resolve("fix-selftest.json.progress")
                // The backend runs the self-test once its model has the batch's diagnostics (the worker analyzes ~30 projects first).
                Files.writeString(solutionDir.resolve("fix-selftest.request"), "")
                runCatching {
                    waitFor("self-test report ($batch)", timeout = 40.minutes, interval = 15.seconds) { Files.exists(report) }
                }.onFailure {
                    println("self-test progress ($batch): " + (if (Files.exists(progress)) Files.readAllLines(progress).takeLast(5) else "not started"))
                    throw it
                }
                Files.copy(report, reports.resolve("fix-selftest-$batch.json"), StandardCopyOption.REPLACE_EXISTING)
            }
        }
    }
}
