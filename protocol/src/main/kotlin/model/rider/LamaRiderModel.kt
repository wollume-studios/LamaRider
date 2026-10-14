package model.rider

import com.jetbrains.rd.generator.nova.*
import com.jetbrains.rd.generator.nova.PredefinedType.*
import com.jetbrains.rider.model.nova.ide.SolutionModel

@Suppress("unused")
object LamaRiderModel : Ext(SolutionModel.Solution) {
    init {
        property("status", string.nullable)
    }
}
