package dev.lamarider

import com.intellij.diff.DiffContentFactory
import com.intellij.diff.DiffManager
import com.intellij.diff.requests.SimpleDiffRequest
import com.intellij.diff.util.DiffUtil
import com.intellij.icons.AllIcons
import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.application.runReadAction
import com.intellij.openapi.components.service
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileTypes.FileTypeManager
import com.intellij.openapi.project.DumbAwareToggleAction
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.popup.JBPopup
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.ui.popup.JBPopupListener
import com.intellij.openapi.ui.popup.LightweightWindowEvent
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.util.ui.JBUI
import dev.lamarider.diff.MetalamaDiff
import java.awt.BorderLayout
import java.awt.FlowLayout
import com.intellij.ui.components.ActionLink
import com.intellij.ui.components.JBLabel
import com.intellij.openapi.project.DumbAwareAction
import java.awt.CardLayout
import java.awt.Dimension
import java.io.File
import javax.swing.JPanel

/**
 * Transformed code of one declaration in a popup (like Quick Definition), or its diff with the source (toggle, remembered).
 * Both views use the diff's editors, so Rider highlights them like Metalama Diff. Metalama Diff shows the whole file.
 */
object TransformedCodePopup {
    private const val SHOW_DIFF = "lamarider.transformedCodePopup.diff"

    /** False when the worker has no transformed code for [target] in [sourceFile]. */
    /** [caretAt]: moves the caret there first (Code Vision clicks), so the popup opens next to the declaration. */
    fun show(project: Project, sourceFile: String, target: String, caretAt: Int? = null): Boolean {
        val model = project.service<LamaRiderService>().model ?: return false
        val (file, declaration) = model.transformedDeclaration(sourceFile, target) ?: return false
        val text = File(file.transformedPath).takeIf { it.isFile }?.readText()?.replace("\r\n", "\n") ?: return false
        if (declaration.end > text.length) return false
        val transformed = ModelData.snippet(text, declaration.start, declaration.end)
        val source = sourceSnippet(sourceFile, declaration)

        val disposable = Disposer.newDisposable("LamaRider transformed code popup")
        val fileType = FileTypeManager.getInstance().getFileTypeByFileName(File(sourceFile).name)
        val factory = DiffContentFactory.getInstance()

        val content = factory.create(project, transformed, fileType)
        val viewer = DiffUtil.createEditor(content.document, project, true, true)
        DiffUtil.createEditorHighlighter(project, content)?.let { viewer.highlighter = it }
        viewer.settings.apply {
            isLineNumbersShown = false
            isFoldingOutlineShown = false
            isLineMarkerAreaShown = false
            additionalLinesCount = 0
            isCaretRowShown = false
        }
        viewer.setBorder(JBUI.Borders.empty(4))
        Disposer.register(disposable) { EditorFactory.getInstance().releaseEditor(viewer) }

        val lines = maxOf(transformed.lines().size, source?.lines()?.size ?: 0)
        val columns = maxOf(transformed.lines().maxOf { it.length }, source?.lines()?.maxOfOrNull { it.length } ?: 0)
        val width = (columns * viewer.colorsScheme.editorFontSize * 0.62 + 40).toInt().coerceIn(320, 900)
        val height = (lines * viewer.lineHeight + 40).coerceIn(80, 600)

        val cards = JPanel(CardLayout())
        cards.add(viewer.component, "code")
        val root = JPanel(BorderLayout()).apply { add(cards, BorderLayout.CENTER) }
        var popup: JBPopup? = null
        fun showView(diff: Boolean) {
            (cards.layout as CardLayout).show(cards, if (diff) "diff" else "code")
            root.preferredSize = Dimension(if (diff) width * 2 else width, height + if (diff) 40 else 0)
            popup?.pack(true, true)
        }
        val actions = DefaultActionGroup()
        if (source != null) {
            val diff = DiffManager.getInstance().createRequestPanel(project, disposable, null)
            diff.setRequest(SimpleDiffRequest(null, factory.create(project, source, fileType), factory.create(project, transformed, fileType), "Source", "Transformed"))
            cards.add(diff.component, "diff")
            val toggle = object : DumbAwareToggleAction("Show Diff with Source", "Source and transformed code side by side", AllIcons.Actions.Diff) {
                override fun getActionUpdateThread() = ActionUpdateThread.EDT
                override fun isSelected(e: AnActionEvent) = PropertiesComponent.getInstance().getBoolean(SHOW_DIFF)
                override fun setSelected(e: AnActionEvent, state: Boolean) {
                    PropertiesComponent.getInstance().setValue(SHOW_DIFF, state)
                    showView(state)
                }
            }
            actions.add(toggle)
        }
        actions.add(object : DumbAwareAction("Whole File in Metalama Diff", "The whole transformed file next to its source", AllIcons.Actions.ListFiles) {
            override fun actionPerformed(e: AnActionEvent) {
                popup?.cancel()
                MetalamaDiff.open(project, sourceFile, declaration.sourceStart ?: 0)
            }
        })
        val toolbar = ActionManager.getInstance().createActionToolbar("LamaRider.TransformedCodePopup", actions, true)
        toolbar.targetComponent = root
        root.add(toolbar.component, BorderLayout.NORTH)
        showView(source != null && PropertiesComponent.getInstance().getBoolean(SHOW_DIFF))

        // The aspects that changed it, as links (what a click does: Settings › Tools › LamaRider).
        val aspects = model.instancesOn(target).map { it.aspectClass }.distinct()
        if (aspects.isNotEmpty()) {
            val links = JPanel(FlowLayout(FlowLayout.LEFT, 8, 2)).apply { border = JBUI.Borders.empty(2, 4) }
            links.add(JBLabel("Aspects:"))
            for (id in aspects) {
                links.add(ActionLink(model.aspectClass(id)?.name ?: id.substringAfterLast('.')) {
                    popup?.cancel()
                    AspectClick.run(project, id)
                }.apply { toolTipText = model.aspectClass(id)?.summary })
            }
            root.add(links, BorderLayout.SOUTH)
        }
        popup = JBPopupFactory.getInstance()
            .createComponentPopupBuilder(root, viewer.contentComponent)
            .setTitle("Transformed: ${target.substringAfter(':').substringBefore('(').substringAfterLast('.')}")
            .setResizable(true)
            .setMovable(true)
            .setRequestFocus(true)
            .setCancelOnClickOutside(true)
            .addListener(object : JBPopupListener {
                override fun onClosed(event: LightweightWindowEvent) = Disposer.dispose(disposable)
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

    /** The declaration as written (current editor text), or null when an aspect introduced it. */
    private fun sourceSnippet(sourceFile: String, declaration: TransformedDeclaration): String? {
        val start = declaration.sourceStart ?: return null
        val end = declaration.sourceEnd ?: return null
        val vf = LocalFileSystem.getInstance().findFileByPath(sourceFile) ?: return null
        val text = runReadAction { FileDocumentManager.getInstance().getDocument(vf)?.text } ?: return null
        return if (end <= text.length) ModelData.snippet(text, start, end) else null
    }

    /** Popup when possible, otherwise the whole-file diff. */
    fun showOrDiff(project: Project, sourceFile: String, target: String, offset: Int) {
        if (!show(project, sourceFile, target)) MetalamaDiff.open(project, sourceFile, offset)
    }
}
