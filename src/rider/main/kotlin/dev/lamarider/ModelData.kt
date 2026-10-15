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
) {
    /** "Add(int, int)" rather than "Calculator.Add(int, int)" (the type is shown above it); parameters as "Open(string) › address". */
    val memberName: String get() = targetName.removePrefix(targetType.substringAfterLast('.') + ".").replace("/", " › ")
}

data class TransformationInfo(val aspectClass: String, val target: String, val kind: String, val description: String, val location: SourceLocation?)

data class DiagnosticInfo(val id: String, val severity: String, val message: String, val target: String?, val file: String?, val line: Int?)

/** A type or member in a transformed file: doc id, LF offsets there and in the source file (null when an aspect introduced it). */
data class TransformedDeclaration(val target: String, val start: Int, val end: Int, val sourceStart: Int? = null, val sourceEnd: Int? = null)

// declarations: null in models written before the worker recorded them (Gson ignores Kotlin defaults).
data class TransformedFileInfo(val file: String, val transformedPath: String, val declarations: List<TransformedDeclaration>? = null)

object AspectModelIds {
    /** The member a parameter target ("M:...;Parameter=0") belongs to; other ids unchanged. */
    fun owner(target: String) = target.substringBefore(';')
}

data class ModelData(
    val aspectClasses: List<AspectClassInfo> = emptyList(),
    val instances: List<AspectInstanceInfo> = emptyList(),
    val transformations: List<TransformationInfo> = emptyList(),
    val diagnostics: List<DiagnosticInfo> = emptyList(),
    val transformedFiles: List<TransformedFileInfo> = emptyList(),
) {
    fun instancesOf(aspectClassId: String) = instances.filter { it.aspectClass == aspectClassId }.distinctBy { it.target }
    /** Includes aspects on the parameters of [target] ("...;Parameter=0"), shown with their method. */
    fun instancesOn(target: String) = instances.filter { it.target == target || it.target.substringBefore(';') == target }
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

    /** Errors for the editor info bar: those without a location; located ones already have Rider's squiggles. */
    fun bannerErrors() = errors().filter { it.file == null }

    /** Editor info bar text; null without such errors. The first error leads since the bar truncates. */
    fun errorBannerText(): String? {
        val errors = bannerErrors()
        val first = errors.firstOrNull() ?: return null
        return "Metalama: ${first.id} ${first.message.lineSequence().first()}" +
            (if (errors.size > 1) " (+${errors.size - 1} more)" else "") + ". Aspects are not applied."
    }

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

    /** Transformed code of [target] (a type or member doc id) declared in source [file]. */
    fun transformedDeclaration(file: String, target: String): Pair<TransformedFileInfo, TransformedDeclaration>? =
        transformedFiles.filter { normalize(it.file) == normalize(file) }
            .firstNotNullOfOrNull { f -> f.declarations?.firstOrNull { it.target == target }?.let { f to it } }

    companion object {
        /** [text] from [start] to [end], re-indented: the first line's column becomes 0, nested lines keep their relative indent. */
        fun snippet(text: String, start: Int, end: Int): String {
            val lineStart = text.lastIndexOf('\n', start - 1) + 1
            val lines = (" ".repeat(start - lineStart) + text.substring(start, end)).lines()
            val indent = lines.filter { it.isNotBlank() }.minOfOrNull { line -> line.takeWhile { it == ' ' || it == '\t' }.length } ?: 0
            return lines.joinToString("\n") { it.drop(indent).trimEnd() }.trim('\n')
        }

        // ponytail: case-insensitive, same as the backend
        private fun normalize(path: String) = path.replace('\\', '/').lowercase()

        fun load(path: String): ModelData = File(path).reader().use { Gson().fromJson(it, ModelData::class.java) }
    }
}
