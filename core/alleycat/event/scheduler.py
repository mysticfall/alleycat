from datetime import datetime, timedelta
from enum import Enum
from logging import Logger, getLogger
from queue import PriorityQueue
from time import mktime
from typing import Final, Optional, TypeVar

import bge
from reactivex import Observable
from reactivex.abc import ScheduledAction
from reactivex.abc.scheduler import AbsoluteTime, RelativeTime
from reactivex.disposable import Disposable
from reactivex.scheduler import ScheduledItem
from reactivex.scheduler.periodicscheduler import PeriodicScheduler
from reactivex.subject import Subject

DELTA_ZERO: Final = timedelta(0)

TState = TypeVar("TState")


class TimeMode(Enum):
    Frame = 0
    Clock = 1
    Real = 2


class EventLoopScheduler(Disposable, PeriodicScheduler):
    logger: Final[Logger]

    def __init__(self, init_time: Optional[datetime] = None, mode: TimeMode = TimeMode.Frame) -> None:
        super().__init__()

        self.logger = getLogger()

        self.__queue: PriorityQueue[ScheduledItem] = PriorityQueue()
        self.__init_time = mktime((init_time if init_time else datetime.now()).timetuple())

        if mode == TimeMode.Frame:
            self.__timer = bge.logic.getFrameTime
        elif mode == TimeMode.Clock:
            self.__timer = bge.logic.getClockTime
        elif mode == TimeMode.Real:
            self.__timer = bge.logic.getRealTime
        else:
            assert False

        self.logger.info("Creating a scheduler with timer: %s (init_time: %s).", mode.name, self.__init_time)

        self.__on_process = Subject[datetime]()

    @property
    def now(self) -> datetime:
        return datetime.fromtimestamp(self.__init_time + self.__timer())

    def schedule(self, action: ScheduledAction, state: Optional[TState] = None) -> Disposable:
        return self.schedule_absolute(self.now, action, state)

    def schedule_relative(self,
                          due: RelativeTime,
                          action: ScheduledAction,
                          state: Optional[TState] = None) -> Disposable:
        due = max(DELTA_ZERO, self.to_timedelta(due))

        return self.schedule_absolute(self.now + due, action, state)

    def schedule_absolute(self,
                          due: AbsoluteTime,
                          action: ScheduledAction,
                          state: Optional[TState] = None) -> Disposable:
        item = ScheduledItem(self, state, action, self.to_datetime(due))

        self.__queue.put(item)

        return Disposable(item.cancel)

    def peek(self) -> Optional[ScheduledItem]:
        return None if self.__queue.empty() else self.__queue.queue[0]

    def process(self) -> None:
        item = self.peek()

        now = self.now

        self.__on_process.on_next(now)

        while item and item.duetime <= now:
            item = self.__queue.get()

            if not item.is_cancelled():
                item.invoke()

            item = self.peek()

    @property
    def on_process(self) -> Observable[datetime]:
        return self.__on_process

    def dispose(self) -> None:
        self.logger.info("Disposing scheduler instance.")

        self.__on_process.dispose()

        super().dispose()
