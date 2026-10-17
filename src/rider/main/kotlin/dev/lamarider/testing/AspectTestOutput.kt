package dev.lamarider.testing

import java.io.File

/** A Metalama aspect test, its expected .t.cs snapshot and the output of its last run. */
object AspectTestOutput {
    /** [output]: obj/transformed/<tfm>/<path in project>/Name.t.cs of the newest run, null before the first run. */
    data class Files(val test: File, val snapshot: File, val output: File?, val projectDir: File) {
        /** Where Metalama's test framework writes the outputs of all runs. */
        val outputDir get() = File(projectDir, "obj/transformed")

        /** The last run's output is not what the snapshot expects (a missing snapshot differs). */
        fun differs(): Boolean = output != null && (!snapshot.isFile || normalized(snapshot.readText()) != normalized(output.readText()))
    }

    /** For a test file or its .t.cs snapshot in a project that references Metalama.Testing.AspectTesting. */
    fun of(file: File): Files? {
        val test = if (file.name.endsWith(".t.cs")) File(file.parentFile, file.name.removeSuffix(".t.cs") + ".cs") else file
        if (test.extension != "cs") return null
        val csproj = generateSequence(test.parentFile) { it.parentFile }
            .firstNotNullOfOrNull { dir -> dir.listFiles { f -> f.extension == "csproj" }?.firstOrNull() } ?: return null
        if (!csproj.readText().contains("Metalama.Testing.AspectTesting", ignoreCase = true)) return null
        val snapshot = File(test.parentFile, test.nameWithoutExtension + ".t.cs")
        val relative = snapshot.relativeTo(csproj.parentFile).path
        val output = File(csproj.parentFile, "obj/transformed").listFiles { f -> f.isDirectory }
            ?.map { File(it, relative) }?.filter { it.isFile }?.maxByOrNull { it.lastModified() }
        return Files(test, snapshot, output, csproj.parentFile)
    }

    /** Line endings and trailing whitespace don't count, as in Metalama's own comparison. */
    fun normalized(text: String) = text.replace("\r\n", "\n").lines().joinToString("\n") { it.trimEnd() }.trimEnd()
}
