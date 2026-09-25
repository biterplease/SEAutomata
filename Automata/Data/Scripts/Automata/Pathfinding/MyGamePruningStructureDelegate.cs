using Sandbox.Game.Entities;
using System.Collections.Generic;
using VRage.Game.Entity;
using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Delegate interface for MyGamePruningStructure operations
    /// Allows mocking of static game methods during testing
    /// </summary>
    public interface IMyGamePruningStructureDelegate
    {
        void GetTopmostEntitiesOverlappingRay(
            ref LineD line,
            List<MyLineSegmentOverlapResult<MyEntity>> result,
            MyEntityQueryType queryType = MyEntityQueryType.Both);

        void GetTopMostEntitiesInBox(ref BoundingBoxD boundingBox, List<MyEntity> result, MyEntityQueryType queryType);
    }

    /// <summary>
    /// Production implementation using real SE API
    /// </summary>
    public class MyGamePruningStructureDelegate : IMyGamePruningStructureDelegate
    {
        public void GetTopmostEntitiesOverlappingRay(
            ref LineD line,
            List<MyLineSegmentOverlapResult<MyEntity>> result,
            MyEntityQueryType queryType = MyEntityQueryType.Both)
        {
            MyGamePruningStructure.GetTopmostEntitiesOverlappingRay(ref line, result, queryType);
        }
        public void GetTopMostEntitiesInBox(ref BoundingBoxD boundingBox, List<MyEntity> result, MyEntityQueryType queryType)
        {
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref boundingBox, result, queryType);
        }
    }
}
