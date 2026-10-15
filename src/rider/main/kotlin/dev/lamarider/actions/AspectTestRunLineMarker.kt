package dev.lamarider.actions

import com.intellij.execution.lineMarker.RunLineMarkerContributor
import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.psi.PsiElement

/** The usual run icon in the gutter of an aspect test file (on its first type): Run / Debug Metalama Aspect Test. */
class AspectTestRunLineMarker : RunLineMarkerContributor() {
    private val firstType = Regex("""\b(class|record|struct|interface|enum)\s""")

    override fun getInfo(element: PsiElement): Info? {
        if (element.firstChild != null) return null // leaves only: one marker per line
        val file = element.containingFile ?: return null
        val type = firstType.find(file.text) ?: return null
        if (element.textRange.startOffset != type.range.first) return null
        val virtualFile = file.virtualFile ?: return null
        if (!RunAspectTestActionBase.isAspectTestFile(virtualFile)) return null
        val actions = ActionManager.getInstance()
        return Info(AllIcons.RunConfigurations.TestState.Run, arrayOf(actions.getAction("LamaRider.RunAspectTest"), actions.getAction("LamaRider.DebugAspectTest"))) {
            "Run Metalama aspect test"
        }
    }
}
