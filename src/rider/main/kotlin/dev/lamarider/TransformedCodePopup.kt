package dev.lamarider

import com.intellij.openapi.components.service
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.EditorKind
import com.intellij.openapi.editor.ex.EditorEx
import com.intellij.openapi.editor.highlighter.EditorHighlighterFactory
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileTypes.FileTypeManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.ui.popup.JBPopupListener
import com.intellij.openapi.ui.popup.LightweightWindowEvent
import com.intellij.util.ui.JBUI
import dev.lamarider.diff.MetalamaDiff
import java.awt.Dimension
import java.io.File

/** Transformed code of one declaration in a popup (like Quick Definition); Metalama Diff shows the whole file. */
object TransformedCodePopup {
    /** False when the worker has no transformed code for [target] in [sourceFile]. */
    /** [caretAt]: moves the caret there first (Code Vision clicks), so the popup opens next to the declaration. */
    fun show(project: Project, sourceFile: String, target: String, caretAt: Int? = null): Boolean {
        val model = project.service<LamaRiderService>().model ?: return false
        val (file, declaration) = model.transformedDeclaration(sourceFile, target) ?: return false
        val text = File(file.transformedPath).takeIf { it.isFile }?.readText()?.replace("\r\n", "\n") ?: return false
        if (declaration.end > text.length) return false
        val code = ModelData.snippet(text, declaration.start, declaration.end)

        val document = EditorFactory.getInstance().createDocument(code)
        val viewer = EditorFactory.getInstance().createViewer(document, project, EditorKind.PREVIEW) as EditorEx
        val fileType = FileTypeManager.getInstance().getFileTypeByFileName(File(sourceFile).name)
        viewer.highlighter = EditorHighlighterFactory.getInstance().createEditorHighlighter(project, fileType)
        viewer.settings.apply {
            isLineNumbersShown = false
            isFoldingOutlineShown = false
            isLineMarkerAreaShown = false
            additionalLinesCount = 0
            isCaretRowShown = false
        }
        viewer.setBorder(JBUI.Borders.empty(4))
        val lineHeight = viewer.lineHeight
        val width = code.lines().maxOf { it.length } * viewer.colorsScheme.editorFontSize * 0.62 + 40
        viewer.component.preferredSize = Dimension(width.toInt().coerceIn(320, 900), (document.lineCount * lineHeight + 16).coerceIn(60, 560))

        val aspects = model.instancesOn(target).mapNotNull { i -> model.aspectClass(i.aspectClass)?.name ?: i.aspectClass.substringAfterLast('.') }.distinct()
        val popup = JBPopupFactory.getInstance()
            .createComponentPopupBuilder(viewer.component, viewer.contentComponent)
            .setTitle("Transformed: ${target.substringAfter(':').substringBefore('(').substringAfterLast('.')}")
            .setAdText(if (aspects.isEmpty()) "All aspects applied" else "Aspects: ${aspects.joinToString(", ")}")
            .setResizable(true)
            .setMovable(true)
            .setRequestFocus(true)
            .setCancelOnClickOutside(true)
            .addListener(object : JBPopupListener {
                override fun onClosed(event: LightweightWindowEvent) = EditorFactory.getInstance().releaseEditor(viewer)
            })
            .createPopup()
        val editor = FileEditorManager.getInstance(project).selectedTextEditor
        if (editor == null) {
            popup.showCenteredInCurrentWindow(project)
            return true
        }
        if (caretAt != null) editor.caretModel.moveToOffset(caretAt.coerceIn(0, editor.document.textLength))
        popup.showInBestPositionFor(editor)
        return true
    }

    /** Popup when possible, otherwise the whole-file diff. */
    fun showOrDiff(project: Project, sourceFile: String, target: String, offset: Int) {
        if (!show(project, sourceFile, target)) MetalamaDiff.open(project, sourceFile, offset)
    }
}
