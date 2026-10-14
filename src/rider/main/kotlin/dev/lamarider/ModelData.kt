package dev.lamarider

import com.google.gson.Gson
import java.io.File

// Mirrors the worker's model.json (schema 1); only what the frontend needs.

data class SourceLocation(val file: String, val start: Int, val end: Int)

data class AspectClassInfo(
    val id: String, val name: String, val project: String, val isLibrary: Boolean, val location: SourceLocation?,
    /** Project name for solution aspects, assembly/package name for library aspects. */
    val `package`: String? = null,
    val summary: String? = null,
    val docUrl: String? = null,
) {
    val group: String get() = `package`?.takeIf { it.isNotEmpty() } ?: if (isLibrary) "Other libraries" else project
}

/** A project or package with its aspect classes (Aspect Explorer top pane). */
data class AspectGroup(val name: String, val isProject: Boolean, val classes: List<AspectClassInfo>)

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
    /** A contract on a parameter (";Parameter=0" / ";ReturnParameter") transforms its method. */
    fun transformationsOf(instance: AspectInstanceInfo) =
        transformations.filter {
            it.aspectClass == instance.aspectClass && (it.target == instance.target || it.target == instance.target.substringBefore(';'))
        }
    fun diagnosticsOn(target: String) = diagnostics.filter { it.target == target }
    fun aspectClass(id: String) = aspectClasses.firstOrNull { it.id == id }

    /** Projects first, then packages; classes sorted by name; [filter] matches class names (case-insensitive). */
    fun groups(filter: String): List<AspectGroup> =
        aspectClasses
            .filter { filter.isEmpty() || it.name.contains(filter, ignoreCase = true) }
            .groupBy { it.group }
            .map { (name, classes) -> AspectGroup(name, classes.none { it.isLibrary }, classes.sortedBy { it.name.lowercase() }) }
            .sortedWith(compareBy<AspectGroup> { !it.isProject }.thenBy { it.name.lowercase() })

    fun errors() = diagnostics.filter { it.severity == "Error" }

    /** Changes only when the set of Metalama errors changes: used to notify once per new error state. */
    fun errorSignature(): String = errors().joinToString(";") { "${it.id}|${it.message}|${it.file}|${it.line}" }

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
