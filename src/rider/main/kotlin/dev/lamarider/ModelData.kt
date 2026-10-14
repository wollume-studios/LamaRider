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

    companion object {
        fun load(path: String): ModelData = File(path).reader().use { Gson().fromJson(it, ModelData::class.java) }
    }
}
