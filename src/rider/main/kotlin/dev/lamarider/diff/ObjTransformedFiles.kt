package dev.lamarider.diff

import java.io.File

object ObjTransformedFiles {
    /** obj/<Configuration>/<tfm>/metalama/... is written when MetalamaEmitCompilerTransformedFiles=true. */
    fun find(file: String): String? {
        val source = File(file)
        var dir = source.parentFile
        while (dir != null && dir.listFiles { f -> f.extension == "csproj" }.isNullOrEmpty()) dir = dir.parentFile
        val obj = File(dir ?: return null, "obj").takeIf { it.isDirectory } ?: return null
        val metalamaSegment = "${File.separator}metalama${File.separator}"
        return obj.walkTopDown()
            .filter { it.isFile && it.name.equals(source.name, ignoreCase = true) && it.path.contains(metalamaSegment, ignoreCase = true) }
            .maxByOrNull { it.lastModified() }?.path
    }
}
