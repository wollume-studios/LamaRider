package dev.lamarider.debug

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.runWriteAction
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VfsUtilCore
import com.intellij.xdebugger.XDebugProcess
import com.intellij.xdebugger.XDebugSessionListener
import com.intellij.xdebugger.XDebuggerManager
import com.intellij.xdebugger.XDebuggerManagerListener
import com.intellij.xdebugger.breakpoints.XBreakpoint
import com.intellij.xdebugger.breakpoints.XBreakpointManager
import com.intellij.xdebugger.breakpoints.XBreakpointProperties
import com.intellij.xdebugger.breakpoints.XLineBreakpoint
import com.intellij.xdebugger.breakpoints.XLineBreakpointType

/**
 * Breakpoints in aspects, templates and fabrics: the debugger only knows Metalama's compiled copy of that code (see
 * [CompileTimeMaps]), so for each debug session (an aspect test, a build) they are mirrored to the copy and removed after.
 */
class CompileTimeBreakpoints(private val project: Project) : XDebuggerManagerListener {
    private val log = logger<CompileTimeBreakpoints>()

    override fun processStarted(debugProcess: XDebugProcess) {
        val manager = XDebuggerManager.getInstance(project).breakpointManager
        val sources = manager.allBreakpoints.filterIsInstance<XLineBreakpoint<*>>()
            .filter { it.isEnabled && it.fileUrl.endsWith(".cs") }
        if (sources.isEmpty()) return
        val root = CompileTimeMaps.root()
        val targets = sources.flatMap { source ->
            CompileTimeMaps.copiesOf(root, VfsUtilCore.urlToPath(source.fileUrl).replace('/', java.io.File.separatorChar))
                .mapNotNull { copy -> copy.line(source.line)?.let { Triple(source, copy.file.path, it) } }
        }
        if (targets.isEmpty()) return

        val mirrors = mutableListOf<XBreakpoint<*>>()
        ApplicationManager.getApplication().invokeAndWait {
            runWriteAction {
                for ((source, path, line) in targets) {
                    val file = LocalFileSystem.getInstance().refreshAndFindFileByPath(path) ?: continue
                    mirror(manager, source, file, line)?.let(mirrors::add)
                }
            }
        }
        log.info("LamaRider: mirrored ${mirrors.size} breakpoints to Metalama's compiled compile-time code")
        debugProcess.session.addSessionListener(object : XDebugSessionListener {
            override fun sessionStopped() {
                ApplicationManager.getApplication().invokeLater { runWriteAction { mirrors.forEach(manager::removeBreakpoint) } }
            }
        })
    }

    @Suppress("UNCHECKED_CAST")
    private fun mirror(manager: XBreakpointManager, source: XLineBreakpoint<*>, file: com.intellij.openapi.vfs.VirtualFile, line: Int): XBreakpoint<*>? {
        val type = source.type as XLineBreakpointType<XBreakpointProperties<*>>
        if (manager.findBreakpointsAtLine(type, file, line).isNotEmpty()) return null // the user's own, after a first stop there
        return manager.addLineBreakpoint(type, file.url, line, type.createBreakpointProperties(file, line)).also {
            it.conditionExpression = source.conditionExpression
        }
    }
}
