package dev.lamarider.explorer

import com.intellij.ide.DefaultTreeExpander
import com.intellij.ide.actions.CollapseAllAction
import com.intellij.ide.actions.ExpandAllAction
import com.intellij.openapi.Disposable
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.SimpleToolWindowPanel
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.ui.DocumentAdapter
import com.intellij.ui.OnePixelSplitter
import com.intellij.ui.SearchTextField
import com.intellij.ui.components.JBCheckBox
import com.intellij.ui.dsl.listCellRenderer.textListCellRenderer
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBList
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.treeStructure.Tree
import com.intellij.util.ui.tree.TreeUtil
import dev.lamarider.AspectClassInfo
import dev.lamarider.AspectInstanceInfo
import dev.lamarider.DiagnosticInfo
import dev.lamarider.LamaRiderService
import dev.lamarider.SourceLocation
import dev.lamarider.TransformationInfo
import dev.lamarider.diff.MetalamaDiff
import java.awt.BorderLayout
import java.awt.event.MouseAdapter
import java.awt.event.MouseEvent
import javax.swing.JPanel
import javax.swing.event.DocumentEvent
import javax.swing.tree.DefaultMutableTreeNode
import javax.swing.tree.DefaultTreeModel

/** Top: aspect classes. Bottom: their targets as Project › Namespace › Type › Member, with transformations and diagnostics. */
class AspectExplorerPanel(private val project: Project, parent: Disposable) : SimpleToolWindowPanel(true, true) {
    private val service = project.service<LamaRiderService>()
    private val search = SearchTextField(false)
    private val includeLibraries = JBCheckBox("Include library aspects")
    private val classes = JBList<AspectClassInfo>()
    private val root = DefaultMutableTreeNode()
    private val tree = Tree(DefaultTreeModel(root))
    private val status = JBLabel()

    init {
        classes.cellRenderer = textListCellRenderer<AspectClassInfo> { c ->
            val count = service.model?.instancesOf(c.id)?.size ?: 0
            "${c.name}  ($count)" + if (c.isLibrary) "  — library" else "  — ${c.project}"
        }
        classes.addListSelectionListener { if (!it.valueIsAdjusting) rebuildTree() }
        search.addDocumentListener(object : DocumentAdapter() {
            override fun textChanged(e: DocumentEvent) = rebuildClasses()
        })
        includeLibraries.addActionListener { rebuildClasses() }
        tree.isRootVisible = false
        tree.addMouseListener(object : MouseAdapter() {
            override fun mouseClicked(e: MouseEvent) {
                if (e.clickCount == 2) activate((tree.lastSelectedPathComponent as? DefaultMutableTreeNode)?.userObject)
            }
        })

        val top = JPanel(BorderLayout()).apply {
            add(JPanel(BorderLayout()).apply {
                add(search, BorderLayout.CENTER)
                add(includeLibraries, BorderLayout.EAST)
            }, BorderLayout.NORTH)
            add(JBScrollPane(classes), BorderLayout.CENTER)
        }
        val splitter = OnePixelSplitter(true, 0.4f).apply {
            firstComponent = top
            secondComponent = JBScrollPane(tree)
        }
        setContent(JPanel(BorderLayout()).apply {
            add(splitter, BorderLayout.CENTER)
            add(status, BorderLayout.SOUTH)
        })

        val expander = DefaultTreeExpander(tree)
        val actions = DefaultActionGroup(
            ActionManager.getInstance().getAction("LamaRider.Refresh"),
            ExpandAllAction { expander },
            CollapseAllAction { expander },
        )
        toolbar = ActionManager.getInstance().createActionToolbar("AspectExplorer", actions, true)
            .apply { targetComponent = this@AspectExplorerPanel }.component

        service.addListener(parent) { rebuildClasses() }
        current = this
        Disposer.register(parent) { if (current === this) current = null }
        rebuildClasses()
    }

    fun select(aspectClassId: String) {
        search.text = ""
        if (service.model?.aspectClass(aspectClassId)?.isLibrary == true) includeLibraries.isSelected = true
        rebuildClasses()
        val index = (0 until classes.model.size).firstOrNull { classes.model.getElementAt(it).id == aspectClassId } ?: return
        classes.selectedIndex = index
        classes.ensureIndexIsVisible(index)
    }

    private fun rebuildClasses() {
        status.text = service.status
        val selected = classes.selectedValue?.id
        val filter = search.text.trim()
        val items = service.model?.aspectClasses.orEmpty()
            .filter { includeLibraries.isSelected || !it.isLibrary }
            .filter { filter.isEmpty() || it.name.contains(filter, ignoreCase = true) }
        classes.setListData(items.toTypedArray())
        items.indexOfFirst { it.id == selected }.takeIf { it >= 0 }?.let { classes.selectedIndex = it }
        rebuildTree()
    }

    private fun rebuildTree() {
        root.removeAllChildren()
        val model = service.model
        val aspect = classes.selectedValue
        if (model != null && aspect != null) {
            for ((projectName, inProject) in model.instancesOf(aspect.id).groupBy { it.project }.toSortedMap()) {
                val projectNode = DefaultMutableTreeNode(projectName).also(root::add)
                for ((ns, inNs) in inProject.groupBy { it.targetType.substringBeforeLast('.', "<global>") }.toSortedMap()) {
                    val nsNode = DefaultMutableTreeNode(ns).also(projectNode::add)
                    for ((type, inType) in inNs.groupBy { it.targetType.substringAfterLast('.') }.toSortedMap()) {
                        val typeNode = DefaultMutableTreeNode(type).also(nsNode::add)
                        for (instance in inType.sortedBy { it.targetName }) {
                            val node = DefaultMutableTreeNode(TargetNode(instance)).also(typeNode::add)
                            model.transformationsOf(instance).forEach { node.add(DefaultMutableTreeNode(TransformationNode(it))) }
                            model.diagnosticsOn(instance.target).forEach { node.add(DefaultMutableTreeNode(DiagnosticNode(it))) }
                        }
                    }
                }
            }
        }
        (tree.model as DefaultTreeModel).reload()
        TreeUtil.expandAll(tree)
    }

    private fun activate(node: Any?) {
        when (node) {
            is TargetNode -> Navigation.open(project, node.instance.location)
            is TransformationNode -> node.transformation.location?.let { MetalamaDiff.open(project, it.file, it.start) }
            is DiagnosticNode -> node.diagnostic.file?.let { Navigation.openLine(project, it, (node.diagnostic.line ?: 1) - 1) }
        }
    }

    private class TargetNode(val instance: AspectInstanceInfo) {
        override fun toString() = "${instance.targetName}  [${instance.appliedVia}]"
    }

    private class TransformationNode(val transformation: TransformationInfo) {
        override fun toString() = transformation.description
    }

    private class DiagnosticNode(val diagnostic: DiagnosticInfo) {
        override fun toString() = "${diagnostic.severity} ${diagnostic.id}: ${diagnostic.message}"
    }

    companion object {
        var current: AspectExplorerPanel? = null
            private set
    }
}

object Navigation {
    fun open(project: Project, location: SourceLocation?) {
        val file = location?.let { LocalFileSystem.getInstance().findFileByPath(it.file) } ?: return
        OpenFileDescriptor(project, file, location.start).navigate(true)
    }

    fun openLine(project: Project, path: String, line: Int) {
        val file = LocalFileSystem.getInstance().findFileByPath(path) ?: return
        OpenFileDescriptor(project, file, line.coerceAtLeast(0), 0).navigate(true)
    }
}
