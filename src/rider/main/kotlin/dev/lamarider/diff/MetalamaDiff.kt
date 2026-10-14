package dev.lamarider.diff

import com.intellij.diff.DiffContentFactory
import com.intellij.diff.DiffManager
import com.intellij.diff.requests.SimpleDiffRequest
import com.intellij.diff.util.DiffUserDataKeys
import com.intellij.diff.util.Side
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.components.service
import com.intellij.openapi.application.runReadAction
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Pair
import com.intellij.openapi.vfs.LocalFileSystem
import dev.lamarider.LamaRiderService
import java.io.File

object MetalamaDiff {
    fun open(project: Project, file: String, offset: Int = 0) {
        val source = LocalFileSystem.getInstance().findFileByPath(file) ?: return
        val transformedPath = project.service<LamaRiderService>().transformedPathFor(file) ?: ObjTransformedFiles.find(file)
        if (transformedPath == null) {
            NotificationGroupManager.getInstance().getNotificationGroup("LamaRider").createNotification(
                "No Metalama transformations in ${source.name}",
                "If aspects should apply here, build the solution or refresh the Aspect Explorer. " +
                    "Without the worker, set <code>MetalamaEmitCompilerTransformedFiles</code> to <code>true</code> in the project.",
                NotificationType.INFORMATION,
            ).notify(project)
            return
        }

        val factory = DiffContentFactory.getInstance()
        val transformedText = File(transformedPath).readText().replace("\r\n", "\n")
        val request = SimpleDiffRequest(
            "Metalama Diff: ${source.name}",
            factory.create(project, source),
            factory.create(project, transformedText, source.fileType),
            "Source",
            "Transformed (read-only)",
        )
        val line = runReadAction { FileDocumentManager.getInstance().getDocument(source)?.let { doc ->
            doc.getLineNumber(offset.coerceIn(0, doc.textLength))
        } } ?: 0
        request.putUserData(DiffUserDataKeys.SCROLL_TO_LINE, Pair.create(Side.LEFT, line))
        DiffManager.getInstance().showDiff(project, request)
    }
}
