package dev.lamarider

import com.google.gson.Gson
import java.io.File

// Mirrors the worker's model.json (schema 1); only what the frontend needs.

data class SourceLocation(val file: String, val start: Int, val end: Int)

data class AspectClassInfo(val id: String, val name: String, val project: String, val isLibrary: Boolean, val location: SourceLocation?)

data class AspectInstanceInfo(
    val aspectClass: String, val target: String, val targetName: String, val targetType: String,
    val targetKind: String, val project: String, val appliedVia: String, val location: SourceLocation?,
)

data class TransformationInfo(val aspectClass: String, val target: String, val kind: String, val description: String, val location: SourceLocation?)

data class DiagnosticInfo(val id: String, val severity: String, val message: String, val target: String?, val file: String?, val line: Int?)

data class TransformedFileInfo(val file: String, val transformedPath: String)

data class ModelData(
    val aspectClasses: List<AspectClassInfo> = emptyList(),
    val instances: List<AspectInstanceInfo> = emptyList(),
    val transformations: List<TransformationInfo> = emptyList(),
    val diagnostics: List<DiagnosticInfo> = emptyList(),
    val transformedFiles: List<TransformedFileInfo> = emptyList(),
) {
    fun instancesOf(aspectClassId: String) = instances.filter { it.aspectClass == aspectClassId }.distinctBy { it.target }
    fun instancesOn(target: String) = instances.filter { it.target == target }
    fun transformationsOf(instance: AspectInstanceInfo) =
        transformations.filter { it.target == instance.target && it.aspectClass == instance.aspectClass }
    fun diagnosticsOn(target: String) = diagnostics.filter { it.target == target }
    fun aspectClass(id: String) = aspectClasses.firstOrNull { it.id == id }

    /** Target id under the caret: the narrowest aspect target or aspect class whose span contains [offset]. */
    fun targetAt(file: String, offset: Int): String? {
        val path = normalize(file)
        val spans = instances.mapNotNull { i -> i.location?.let { it to i.target } } +
            aspectClasses.mapNotNull { c -> c.location?.let { it to c.id } }
        return spans
            .filter { (loc, _) -> normalize(loc.file) == path && offset >= loc.start && offset < loc.end }
            .minByOrNull { (loc, _) -> loc.end - loc.start }
            ?.second
    }

    companion object {
        // ponytail: case-insensitive, same as the backend
        private fun normalize(path: String) = path.replace('\\', '/').lowercase()

        fun load(path: String): ModelData = File(path).reader().use { Gson().fromJson(it, ModelData::class.java) }
    }
}
