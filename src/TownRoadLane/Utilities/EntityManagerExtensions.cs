using Game.Common;
using Unity.Entities;

namespace TownRoadLane
{
    public static class EntityManagerExtensions
    {
        /// <summary>Tags the entity Updated unless it already is, so the game and the mod's own
        /// rebuild systems pick up the change this frame.</summary>
        public static void MarkUpdated(this EntityManager em, Entity entity)
        {
            if (!em.HasComponent<Updated>(entity))
                em.AddComponent<Updated>(entity);
        }
    }
}
